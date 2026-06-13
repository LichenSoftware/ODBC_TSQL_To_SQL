using System.Data;
using System.Data.Common;
using System.Security.Cryptography;
using System.Text;
using System.Xml;
using Microsoft.Extensions.Logging;
using MigrationAssessment.Core.Interfaces;
using MigrationAssessment.Core.Models;

namespace MigrationAssessment.Collectors;

/// <summary>
/// Collects SQL statements from a SQL Server Extended Events session.
/// Supports ring_buffer and file targets, capturing ad hoc SQL, stored procedure
/// executions, dynamic SQL, temp table DDL, and TRY/CATCH batches.
/// </summary>
public sealed class ExtendedEventsCollector : IStatementCollector
{
    private readonly ILogger<ExtendedEventsCollector> _logger;
    private readonly string _sessionName;

    /// <summary>
    /// Maximum number of characters preserved for SQL text (Requirement 2.6).
    /// </summary>
    internal const int MaxSqlTextLength = 65_536;

    /// <summary>
    /// Maximum number of characters for parameter values (Requirement 2.2).
    /// </summary>
    internal const int MaxParameterValueLength = 4_000;

    /// <summary>
    /// Maximum number of stored procedure parameters captured (Requirement 2.2).
    /// </summary>
    internal const int MaxParameterCount = 128;

    public ExtendedEventsCollector(ILogger<ExtendedEventsCollector> logger, string sessionName = "migration_assessment")
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _sessionName = sessionName ?? throw new ArgumentNullException(nameof(sessionName));
    }

    /// <inheritdoc />
    public string SourceName => "Extended Events";

    /// <inheritdoc />
    public async Task<CollectionResult> CollectAsync(DbConnection connection, CollectionOptions options, CancellationToken ct)
    {
        // Step 1: Check if XE session exists and is running
        var sessionStatus = await CheckSessionStatusAsync(connection, options, ct).ConfigureAwait(false);

        if (!sessionStatus.IsRunning)
        {
            _logger.LogWarning(
                "Extended Events session '{SessionName}' is {Reason}. Skipping XE collection.",
                _sessionName, sessionStatus.Reason);

            return new CollectionResult
            {
                Statements = Array.Empty<CollectedStatement>(),
                Succeeded = false,
                ErrorMessage = $"Extended Events session '{_sessionName}' is {sessionStatus.Reason}.",
                TotalEventsProcessed = 0
            };
        }

        // Step 2: Try ring_buffer target first, fall back to file target
        var events = await TryCollectFromRingBufferAsync(connection, options, ct).ConfigureAwait(false);

        if (events is null)
        {
            events = await TryCollectFromFileTargetAsync(connection, options, ct).ConfigureAwait(false);
        }

        if (events is null || events.Count == 0)
        {
            _logger.LogWarning("No events found in Extended Events session '{SessionName}'.", _sessionName);
            return new CollectionResult
            {
                Statements = Array.Empty<CollectedStatement>(),
                Succeeded = true,
                TotalEventsProcessed = 0
            };
        }

        // Step 3: Process events, potentially in batches
        var totalEventCount = events.Count;
        if (totalEventCount > 100_000)
        {
            _logger.LogInformation(
                "Extended Events session '{SessionName}' yielded {TotalEvents} events. Processing in batches of {BatchSize}.",
                _sessionName, totalEventCount, options.MaxBatchSize);
        }

        var statements = ProcessEventsInBatches(events, options.MaxBatchSize);

        _logger.LogInformation(
            "Extended Events collection complete. Total events processed: {Total}, statements collected: {Count}.",
            totalEventCount, statements.Count);

        return new CollectionResult
        {
            Statements = statements,
            Succeeded = true,
            TotalEventsProcessed = totalEventCount
        };
    }

    private async Task<SessionStatus> CheckSessionStatusAsync(DbConnection connection, CollectionOptions options, CancellationToken ct)
    {
        const string query = @"
SELECT s.name, s.create_time
FROM sys.dm_xe_sessions s
WHERE s.name = @sessionName";

        using var cmd = connection.CreateCommand();
        cmd.CommandText = query;
        cmd.CommandTimeout = (int)options.QueryTimeout.TotalSeconds;

        var param = cmd.CreateParameter();
        param.ParameterName = "@sessionName";
        param.Value = _sessionName;
        param.DbType = DbType.String;
        cmd.Parameters.Add(param);

        using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);

        if (!await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            return new SessionStatus(false, "not found");
        }

        return new SessionStatus(true, "active");
    }

    private async Task<List<XeEventData>?> TryCollectFromRingBufferAsync(DbConnection connection, CollectionOptions options, CancellationToken ct)
    {
        const string query = @"
SELECT 
    target_data = CAST(t.target_data AS XML)
FROM sys.dm_xe_sessions s
JOIN sys.dm_xe_session_targets t ON s.address = t.event_session_address
WHERE s.name = @sessionName AND t.target_name = 'ring_buffer'";

        try
        {
            using var cmd = connection.CreateCommand();
            cmd.CommandText = query;
            cmd.CommandTimeout = (int)options.QueryTimeout.TotalSeconds;

            var param = cmd.CreateParameter();
            param.ParameterName = "@sessionName";
            param.Value = _sessionName;
            param.DbType = DbType.String;
            cmd.Parameters.Add(param);

            using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);

            if (!await reader.ReadAsync(ct).ConfigureAwait(false))
            {
                _logger.LogDebug("No ring_buffer target found for session '{SessionName}'. Trying file target.", _sessionName);
                return null;
            }

            var xmlData = reader.GetString(0);
            return ParseRingBufferXml(xmlData);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Failed to read ring_buffer target for session '{SessionName}'. Trying file target.", _sessionName);
            return null;
        }
    }

    private async Task<List<XeEventData>?> TryCollectFromFileTargetAsync(DbConnection connection, CollectionOptions options, CancellationToken ct)
    {
        // First, get the file path from the session target
        const string filePathQuery = @"
SELECT 
    CAST(t.target_data AS XML).value('(EventFileTarget/File/@name)[1]', 'nvarchar(4000)') AS file_path
FROM sys.dm_xe_sessions s
JOIN sys.dm_xe_session_targets t ON s.address = t.event_session_address
WHERE s.name = @sessionName AND t.target_name = 'event_file'";

        try
        {
            string? filePath;
            using (var cmd = connection.CreateCommand())
            {
                cmd.CommandText = filePathQuery;
                cmd.CommandTimeout = (int)options.QueryTimeout.TotalSeconds;

                var param = cmd.CreateParameter();
                param.ParameterName = "@sessionName";
                param.Value = _sessionName;
                param.DbType = DbType.String;
                cmd.Parameters.Add(param);

                using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
                if (!await reader.ReadAsync(ct).ConfigureAwait(false))
                {
                    _logger.LogDebug("No file target found for session '{SessionName}'.", _sessionName);
                    return null;
                }

                filePath = reader.IsDBNull(0) ? null : reader.GetString(0);
            }

            if (string.IsNullOrEmpty(filePath))
            {
                _logger.LogDebug("File target path is empty for session '{SessionName}'.", _sessionName);
                return null;
            }

            // Read events from file target
            const string readFileQuery = @"
SELECT event_data
FROM sys.fn_xe_file_target_read_file(@filePath, null, null, null)";

            using var fileCmd = connection.CreateCommand();
            fileCmd.CommandText = readFileQuery;
            fileCmd.CommandTimeout = (int)options.QueryTimeout.TotalSeconds;

            var fileParam = fileCmd.CreateParameter();
            fileParam.ParameterName = "@filePath";
            fileParam.Value = filePath;
            fileParam.DbType = DbType.String;
            fileCmd.Parameters.Add(fileParam);

            using var fileReader = await fileCmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
            var events = new List<XeEventData>();

            while (await fileReader.ReadAsync(ct).ConfigureAwait(false))
            {
                var eventXml = fileReader.GetString(0);
                var parsedEvent = ParseSingleEventXml(eventXml);
                if (parsedEvent is not null)
                {
                    events.Add(parsedEvent);
                }
            }

            return events;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Failed to read file target for session '{SessionName}'.", _sessionName);
            return null;
        }
    }

    internal List<XeEventData> ParseRingBufferXml(string xmlData)
    {
        var events = new List<XeEventData>();

        if (string.IsNullOrWhiteSpace(xmlData))
            return events;

        var doc = new XmlDocument();
        try
        {
            doc.LoadXml(xmlData);
        }
        catch (XmlException ex)
        {
            _logger.LogWarning(ex, "Failed to parse ring_buffer XML data.");
            return events;
        }

        var eventNodes = doc.SelectNodes("//event");
        if (eventNodes is null)
            return events;

        foreach (XmlNode eventNode in eventNodes)
        {
            var parsed = ParseEventNode(eventNode);
            if (parsed is not null)
            {
                events.Add(parsed);
            }
        }

        return events;
    }

    internal XeEventData? ParseSingleEventXml(string eventXml)
    {
        if (string.IsNullOrWhiteSpace(eventXml))
            return null;

        var doc = new XmlDocument();
        try
        {
            doc.LoadXml(eventXml);
        }
        catch (XmlException ex)
        {
            _logger.LogWarning(ex, "Failed to parse single event XML.");
            return null;
        }

        var eventNode = doc.DocumentElement;
        return eventNode is null ? null : ParseEventNode(eventNode);
    }

    private static XeEventData? ParseEventNode(XmlNode eventNode)
    {
        var eventName = eventNode.Attributes?["name"]?.Value;
        if (string.IsNullOrEmpty(eventName))
            return null;

        // Only process relevant event types
        if (!IsRelevantEventType(eventName))
            return null;

        var timestamp = eventNode.Attributes?["timestamp"]?.Value;
        var parsedTimestamp = timestamp is not null
            ? DateTimeOffset.TryParse(timestamp, out var ts) ? ts : (DateTimeOffset?)null
            : null;

        // Extract data fields
        string? sqlText = null;
        string? databaseName = null;
        string? executingPrincipal = null;
        string? objectName = null;
        var parameters = new List<ProcedureParameter>();

        var dataNodes = eventNode.SelectNodes("data");
        if (dataNodes is not null)
        {
            foreach (XmlNode dataNode in dataNodes)
            {
                var name = dataNode.Attributes?["name"]?.Value;
                var value = dataNode.SelectSingleNode("value")?.InnerText
                         ?? dataNode.SelectSingleNode("text")?.InnerText;

                switch (name)
                {
                    case "batch_text":
                    case "sql_text":
                    case "statement":
                        if (!string.IsNullOrEmpty(value))
                            sqlText = value;
                        break;
                    case "database_name":
                        databaseName = value;
                        break;
                    case "object_name":
                        objectName = value;
                        break;
                }
            }
        }

        // Extract action fields (session_id, username, database_name often in actions)
        var actionNodes = eventNode.SelectNodes("action");
        if (actionNodes is not null)
        {
            foreach (XmlNode actionNode in actionNodes)
            {
                var name = actionNode.Attributes?["name"]?.Value;
                var value = actionNode.SelectSingleNode("value")?.InnerText
                         ?? actionNode.SelectSingleNode("text")?.InnerText;

                switch (name)
                {
                    case "database_name":
                        if (!string.IsNullOrEmpty(value))
                            databaseName = value;
                        break;
                    case "username":
                    case "nt_username":
                    case "server_principal_name":
                    case "session_nt_username":
                        if (!string.IsNullOrEmpty(value))
                            executingPrincipal = value;
                        break;
                }
            }
        }

        if (string.IsNullOrEmpty(sqlText))
            return null;

        // Preserve SQL text up to MaxSqlTextLength (Requirement 2.6)
        if (sqlText.Length > MaxSqlTextLength)
            sqlText = sqlText[..MaxSqlTextLength];

        // For stored procedure events, extract parameters
        if (IsStoredProcedureEvent(eventName) && objectName is not null)
        {
            parameters = ExtractParameters(eventNode);
        }

        return new XeEventData
        {
            EventName = eventName,
            SqlText = sqlText,
            DatabaseName = databaseName,
            ExecutingPrincipal = executingPrincipal,
            Timestamp = parsedTimestamp,
            ObjectName = objectName,
            Parameters = parameters
        };
    }

    private static bool IsRelevantEventType(string eventName)
    {
        return eventName is
            "sql_batch_completed" or
            "sql_batch_starting" or
            "sql_statement_completed" or
            "sql_statement_starting" or
            "sp_statement_completed" or
            "sp_statement_starting" or
            "rpc_completed" or
            "rpc_starting" or
            "module_start" or
            "module_end";
    }

    private static bool IsStoredProcedureEvent(string eventName)
    {
        return eventName is
            "sp_statement_completed" or
            "sp_statement_starting" or
            "rpc_completed" or
            "rpc_starting" or
            "module_start" or
            "module_end";
    }

    private static List<ProcedureParameter> ExtractParameters(XmlNode eventNode)
    {
        var parameters = new List<ProcedureParameter>();

        // Parameters may be in a data node named "parameters" or within the statement text
        var paramNodes = eventNode.SelectNodes("data[@name='parameters']/value/parameter")
                      ?? eventNode.SelectNodes("data[@name='input_parameters']/value/parameter");

        if (paramNodes is not null)
        {
            var count = 0;
            foreach (XmlNode paramNode in paramNodes)
            {
                if (count >= MaxParameterCount) break;

                var paramName = paramNode.Attributes?["name"]?.Value ?? $"@p{count}";
                var paramType = paramNode.Attributes?["type"]?.Value ?? "unknown";
                var paramValue = paramNode.InnerText ?? string.Empty;

                // Truncate parameter values at 4,000 characters (Requirement 2.2)
                if (paramValue.Length > MaxParameterValueLength)
                    paramValue = paramValue[..MaxParameterValueLength];

                parameters.Add(new ProcedureParameter(paramName, paramType, paramValue));
                count++;
            }
        }

        return parameters;
    }

    internal IReadOnlyList<CollectedStatement> ProcessEventsInBatches(List<XeEventData> events, int batchSize)
    {
        var statements = new List<CollectedStatement>();
        var totalEvents = events.Count;
        var processedCount = 0;

        while (processedCount < totalEvents)
        {
            var currentBatchSize = Math.Min(batchSize, totalEvents - processedCount);
            var batch = events.GetRange(processedCount, currentBatchSize);

            foreach (var evt in batch)
            {
                var statement = MapToCollectedStatement(evt);
                if (statement is not null)
                {
                    statements.Add(statement);
                }
            }

            processedCount += currentBatchSize;
        }

        return statements;
    }

    private static CollectedStatement? MapToCollectedStatement(XeEventData evt)
    {
        if (string.IsNullOrEmpty(evt.SqlText))
            return null;

        var sqlText = evt.SqlText;

        // Build enriched SQL text for stored procedure events with parameters
        if (evt.ObjectName is not null && evt.Parameters.Count > 0)
        {
            var paramSuffix = BuildParameterSuffix(evt.Parameters);
            sqlText = $"EXEC {evt.ObjectName} {paramSuffix}";

            // If the original SQL text is more informative, keep it
            if (evt.SqlText.Length > sqlText.Length)
                sqlText = evt.SqlText;
        }

        // Preserve SQL text up to MaxSqlTextLength (Requirement 2.6)
        if (sqlText.Length > MaxSqlTextLength)
            sqlText = sqlText[..MaxSqlTextLength];

        var queryHash = ComputeQueryHash(sqlText);

        return new CollectedStatement
        {
            SqlText = sqlText,
            Source = StatementSource.ExtendedEvents,
            QueryHash = queryHash,
            ExecutionCount = 1,
            DatabaseName = evt.DatabaseName,
            ExecutingPrincipal = evt.ExecutingPrincipal,
            ExecutionTimestamp = evt.Timestamp
        };
    }

    private static string BuildParameterSuffix(List<ProcedureParameter> parameters)
    {
        var sb = new StringBuilder();
        for (int i = 0; i < parameters.Count; i++)
        {
            if (i > 0) sb.Append(", ");
            var p = parameters[i];
            sb.Append(p.Name);
            sb.Append(" = ");

            // Truncate parameter values at 4,000 characters (Requirement 2.2)
            var value = p.Value.Length > MaxParameterValueLength
                ? p.Value[..MaxParameterValueLength]
                : p.Value;
            sb.Append('\'');
            sb.Append(value);
            sb.Append('\'');
        }
        return sb.ToString();
    }

    private static string ComputeQueryHash(string sqlText)
    {
        var bytes = Encoding.UTF8.GetBytes(sqlText);
        var hash = SHA256.HashData(bytes);
        return Convert.ToHexString(hash)[..16];
    }

    /// <summary>
    /// Represents the status of an Extended Events session.
    /// </summary>
    private sealed record SessionStatus(bool IsRunning, string Reason);

    /// <summary>
    /// Represents a parsed Extended Events event with extracted data fields.
    /// </summary>
    internal sealed class XeEventData
    {
        public required string EventName { get; init; }
        public required string SqlText { get; set; }
        public string? DatabaseName { get; init; }
        public string? ExecutingPrincipal { get; init; }
        public DateTimeOffset? Timestamp { get; init; }
        public string? ObjectName { get; init; }
        public List<ProcedureParameter> Parameters { get; init; } = new();
    }

    /// <summary>
    /// Represents a stored procedure parameter extracted from an XE event.
    /// </summary>
    internal sealed record ProcedureParameter(string Name, string Type, string Value);
}
