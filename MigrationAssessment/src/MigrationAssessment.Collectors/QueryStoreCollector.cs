using System.Data.Common;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging;
using MigrationAssessment.Core.Interfaces;
using MigrationAssessment.Core.Models;

namespace MigrationAssessment.Collectors;

/// <summary>
/// Collects SQL statements from SQL Server Query Store.
/// Queries sys.query_store_query_text, sys.query_store_plan, and sys.query_store_runtime_stats
/// to capture query performance data for migration assessment.
/// </summary>
public sealed class QueryStoreCollector : IStatementCollector
{
    private readonly ILogger<QueryStoreCollector> _logger;

    private const string CheckQueryStoreStateSql = """
        SELECT actual_state_desc
        FROM sys.database_query_store_options
        """;

    private const string CollectStatementsSql = """
        SELECT DISTINCT
            qt.query_sql_text,
            q.query_hash,
            p.plan_id,
            p.query_plan_hash,
            rs.count_executions,
            rs.avg_duration,
            rs.avg_cpu_time,
            rs.avg_logical_io_reads
        FROM sys.query_store_query_text qt
        JOIN sys.query_store_query q ON qt.query_text_id = q.query_text_id
        JOIN sys.query_store_plan p ON q.query_id = p.query_id
        JOIN sys.query_store_runtime_stats rs ON p.plan_id = rs.plan_id
        """;

    public QueryStoreCollector(ILogger<QueryStoreCollector> logger)
    {
        _logger = logger;
    }

    /// <inheritdoc />
    public string SourceName => "Query Store";

    /// <inheritdoc />
    public async Task<CollectionResult> CollectAsync(
        DbConnection connection,
        CollectionOptions options,
        CancellationToken ct)
    {
        var timeoutSeconds = (int)options.QueryTimeout.TotalSeconds;

        // Step 1: Check Query Store state
        var state = await GetQueryStoreStateAsync(connection, timeoutSeconds, ct);

        if (state is null or "OFF")
        {
            _logger.LogWarning("Query Store is disabled");
            return new CollectionResult
            {
                Statements = Array.Empty<CollectedStatement>(),
                Succeeded = false,
                ErrorMessage = "Query Store is disabled"
            };
        }

        if (state == "ERROR")
        {
            _logger.LogWarning("Query Store is in ERROR state");
            return new CollectionResult
            {
                Statements = Array.Empty<CollectedStatement>(),
                Succeeded = false,
                ErrorMessage = "Query Store is in ERROR state"
            };
        }

        // State is READ_WRITE or READ_ONLY — proceed with collection
        _logger.LogInformation("Query Store state: {State}. Proceeding with collection", state);

        try
        {
            var statements = await CollectStatementsAsync(connection, timeoutSeconds, ct);

            _logger.LogInformation("Collected {Count} statements from Query Store", statements.Count);

            return new CollectionResult
            {
                Statements = statements,
                Succeeded = true,
                TotalEventsProcessed = statements.Count
            };
        }
        catch (OperationCanceledException)
        {
            _logger.LogWarning(
                "Query Store collection timed out after {Timeout} seconds",
                timeoutSeconds);

            return new CollectionResult
            {
                Statements = Array.Empty<CollectedStatement>(),
                Succeeded = false,
                ErrorMessage = $"Query Store collection timed out after {timeoutSeconds} seconds"
            };
        }
    }

    private async Task<string?> GetQueryStoreStateAsync(
        DbConnection connection,
        int timeoutSeconds,
        CancellationToken ct)
    {
        using var command = connection.CreateCommand();
        command.CommandText = CheckQueryStoreStateSql;
        command.CommandTimeout = timeoutSeconds;

        var result = await command.ExecuteScalarAsync(ct);
        return result?.ToString();
    }

    private async Task<List<CollectedStatement>> CollectStatementsAsync(
        DbConnection connection,
        int timeoutSeconds,
        CancellationToken ct)
    {
        using var command = connection.CreateCommand();
        command.CommandText = CollectStatementsSql;
        command.CommandTimeout = timeoutSeconds;

        var statements = new List<CollectedStatement>();

        using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            var sqlText = reader.GetString(reader.GetOrdinal("query_sql_text"));
            var queryHash = reader.GetValue(reader.GetOrdinal("query_hash"));
            var planId = reader.GetInt64(reader.GetOrdinal("plan_id"));
            var planHash = reader.GetValue(reader.GetOrdinal("query_plan_hash"));
            var executionCount = reader.GetInt64(reader.GetOrdinal("count_executions"));
            var avgDuration = reader.GetDouble(reader.GetOrdinal("avg_duration"));
            var avgCpuTime = reader.GetDouble(reader.GetOrdinal("avg_cpu_time"));
            var avgLogicalReads = reader.GetDouble(reader.GetOrdinal("avg_logical_io_reads"));

            var queryHashStr = ConvertHashToString(queryHash);
            var planHashStr = ConvertHashToString(planHash);

            statements.Add(new CollectedStatement
            {
                SqlText = sqlText,
                Source = StatementSource.QueryStore,
                QueryHash = queryHashStr,
                ExecutionCount = executionCount,
                AvgDurationMs = avgDuration / 1000.0, // Query Store stores duration in microseconds
                CpuMs = avgCpuTime / 1000.0, // Query Store stores CPU time in microseconds
                LogicalReads = (long)avgLogicalReads,
                PlanId = planId,
                PlanHash = planHashStr
            });
        }

        return statements;
    }

    /// <summary>
    /// Converts a query_hash or query_plan_hash value (byte array) to a hex string representation.
    /// </summary>
    private static string ConvertHashToString(object hashValue)
    {
        if (hashValue is byte[] bytes)
        {
            return "0x" + Convert.ToHexString(bytes);
        }

        return hashValue?.ToString() ?? string.Empty;
    }
}
