using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using SchemaConversion.Core.Interfaces;
using SchemaConversion.Core.Models;

namespace SchemaConversion.Orchestration;

/// <summary>
/// Append-only JSON Lines audit log writer with file rotation.
/// Writes one JSON object per line to audit-{seq}.jsonl files.
/// </summary>
public sealed class AuditLogWriter : IAuditLogWriter, IDisposable
{
    private readonly string _auditDirectory;
    private readonly long _maxFileSizeBytes;
    private readonly ILogger<AuditLogWriter> _logger;
    private readonly SemaphoreSlim _writeLock = new(1, 1);

    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
    };

    /// <summary>
    /// Creates a new AuditLogWriter.
    /// </summary>
    /// <param name="auditDirectory">Base directory for audit log files.</param>
    /// <param name="logger">Logger instance.</param>
    /// <param name="maxFileSizeBytes">Maximum file size before rotation. Defaults to 50MB.</param>
    public AuditLogWriter(
        string auditDirectory,
        ILogger<AuditLogWriter> logger,
        long maxFileSizeBytes = 50 * 1024 * 1024)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(auditDirectory);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxFileSizeBytes);

        _auditDirectory = Path.GetFullPath(auditDirectory);
        _maxFileSizeBytes = maxFileSizeBytes;
        _logger = logger;

        Directory.CreateDirectory(_auditDirectory);
    }

    public async Task WriteAsync(AuditLogEntry entry, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(entry);

        // Sanitize: strip any potential sensitive data from the entry before writing.
        // The AuditLogEntry model shouldn't contain credentials, but we ensure timestamps are UTC with ms precision.
        var sanitizedEntry = new AuditLogRecord
        {
            SessionId = entry.SessionId,
            ObjectName = entry.ObjectName,
            ObjectType = entry.ObjectType,
            PromptTemplateVersion = entry.PromptTemplateVersion,
            FullPrompt = entry.FullPrompt,
            ModelId = entry.ModelId,
            FullResponse = entry.FullResponse,
            Timestamp = FormatTimestamp(entry.Timestamp),
            RetryAttempt = entry.RetryAttempt,
            IsError = entry.IsError
        };

        var jsonLine = JsonSerializer.Serialize(sanitizedEntry, SerializerOptions);

        await _writeLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var targetFile = await GetCurrentAuditFileAsync(ct).ConfigureAwait(false);
            await File.AppendAllTextAsync(targetFile, jsonLine + "\n", ct).ConfigureAwait(false);

            _logger.LogDebug("Wrote audit entry for {ObjectName} to {File}",
                entry.ObjectName, Path.GetFileName(targetFile));
        }
        finally
        {
            _writeLock.Release();
        }
    }

    private async Task<string> GetCurrentAuditFileAsync(CancellationToken ct)
    {
        var currentSeq = GetCurrentSequenceNumber();
        var currentFile = GetAuditFilePath(currentSeq);

        if (File.Exists(currentFile))
        {
            var fileInfo = new FileInfo(currentFile);
            if (fileInfo.Length >= _maxFileSizeBytes)
            {
                // Rotate to next file
                currentSeq++;
                currentFile = GetAuditFilePath(currentSeq);
                _logger.LogInformation("Rotating audit log to sequence {Sequence}", currentSeq);
            }
        }

        // Ensure the file exists
        if (!File.Exists(currentFile))
        {
            await File.WriteAllTextAsync(currentFile, string.Empty, ct).ConfigureAwait(false);
        }

        return currentFile;
    }

    private int GetCurrentSequenceNumber()
    {
        if (!Directory.Exists(_auditDirectory))
            return 1;

        var files = Directory.GetFiles(_auditDirectory, "audit-*.jsonl");
        if (files.Length == 0)
            return 1;

        var maxSeq = 0;
        foreach (var file in files)
        {
            var fileName = Path.GetFileNameWithoutExtension(file);
            if (fileName.StartsWith("audit-", StringComparison.Ordinal) &&
                int.TryParse(fileName.AsSpan(6), out var seq))
            {
                maxSeq = Math.Max(maxSeq, seq);
            }
        }

        return maxSeq == 0 ? 1 : maxSeq;
    }

    private string GetAuditFilePath(int sequenceNumber)
    {
        var fileName = $"audit-{sequenceNumber:D3}.jsonl";
        var filePath = Path.Combine(_auditDirectory, fileName);
        PathValidator.ValidateResolvedPath(filePath, _auditDirectory);
        return filePath;
    }

    private static string FormatTimestamp(DateTimeOffset timestamp)
    {
        // UTC with millisecond precision: "2026-07-04T12:15:28.123Z"
        return timestamp.UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ss.fffZ");
    }

    public void Dispose()
    {
        _writeLock.Dispose();
    }

    /// <summary>
    /// Internal record for serializing audit entries with string timestamp.
    /// </summary>
    private sealed record AuditLogRecord
    {
        public required string SessionId { get; init; }
        public required string ObjectName { get; init; }
        public required SchemaObjectType ObjectType { get; init; }
        public required string PromptTemplateVersion { get; init; }
        public required string FullPrompt { get; init; }
        public required string ModelId { get; init; }
        public required string FullResponse { get; init; }
        public required string Timestamp { get; init; }
        public int RetryAttempt { get; init; }
        public bool IsError { get; init; }
    }
}
