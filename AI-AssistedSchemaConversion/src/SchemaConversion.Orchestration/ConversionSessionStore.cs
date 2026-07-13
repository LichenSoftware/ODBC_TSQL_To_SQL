using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using SchemaConversion.Core.Interfaces;
using SchemaConversion.Core.Models;

namespace SchemaConversion.Orchestration;

/// <summary>
/// Directory-based persistence for conversion sessions.
/// Each session is a directory containing session.json metadata and per-object JSON files.
/// </summary>
public sealed class ConversionSessionStore : IConversionSessionStore
{
    private readonly string _baseDirectory;
    private readonly ILogger<ConversionSessionStore> _logger;
    private readonly SemaphoreSlim _sessionMetadataLock = new(1, 1);

    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
    };

    public ConversionSessionStore(string baseDirectory, ILogger<ConversionSessionStore> logger)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(baseDirectory);
        _baseDirectory = Path.GetFullPath(baseDirectory);
        _logger = logger;
    }

    public async Task<ConversionSession> LoadOrCreateAsync(string sessionId, CancellationToken ct)
    {
        PathValidator.ValidateSessionId(sessionId);

        var sessionDir = GetSessionDirectory(sessionId);
        var sessionFile = Path.Combine(sessionDir, "session.json");

        if (File.Exists(sessionFile))
        {
            _logger.LogDebug("Loading existing session {SessionId}", sessionId);
            var json = await File.ReadAllTextAsync(sessionFile, ct).ConfigureAwait(false);
            var session = JsonSerializer.Deserialize<ConversionSession>(json, SerializerOptions);
            return session ?? throw new InvalidOperationException($"Failed to deserialize session {sessionId}");
        }

        _logger.LogInformation("Creating new session {SessionId}", sessionId);

        Directory.CreateDirectory(sessionDir);
        Directory.CreateDirectory(Path.Combine(sessionDir, "objects"));
        Directory.CreateDirectory(Path.Combine(sessionDir, "audit"));

        var newSession = new ConversionSession
        {
            SessionId = sessionId,
            CreatedAt = DateTimeOffset.UtcNow,
            LastModifiedAt = DateTimeOffset.UtcNow,
            TotalObjectCount = 0
        };

        var newJson = JsonSerializer.Serialize(newSession, SerializerOptions);
        await File.WriteAllTextAsync(sessionFile, newJson, ct).ConfigureAwait(false);

        return newSession;
    }

    public async Task SaveEntryAsync(string sessionId, ConversionSessionEntry entry, CancellationToken ct)
    {
        PathValidator.ValidateSessionId(sessionId);
        PathValidator.ValidateNameSegment(entry.Source.SchemaName, nameof(entry.Source.SchemaName));
        PathValidator.ValidateNameSegment(entry.Source.Name, nameof(entry.Source.Name));

        var sessionDir = GetSessionDirectory(sessionId);
        var objectsDir = Path.Combine(sessionDir, "objects");
        Directory.CreateDirectory(objectsDir);

        var fileName = GetObjectFileName(entry.Source.SchemaName, entry.Source.Name, entry.Source.ObjectType);
        var filePath = Path.Combine(objectsDir, fileName);

        PathValidator.ValidateResolvedPath(filePath, _baseDirectory);

        var json = JsonSerializer.Serialize(entry, SerializerOptions);
        await File.WriteAllTextAsync(filePath, json, ct).ConfigureAwait(false);

        // Update session metadata
        await UpdateSessionMetadataAsync(sessionId, ct).ConfigureAwait(false);

        _logger.LogDebug("Saved entry {Schema}.{Name} for session {SessionId}",
            entry.Source.SchemaName, entry.Source.Name, sessionId);
    }

    public async Task<ConversionSessionEntry?> GetEntryAsync(
        string sessionId, string schemaName, string objectName, CancellationToken ct)
    {
        PathValidator.ValidateSessionId(sessionId);
        PathValidator.ValidateNameSegment(schemaName, nameof(schemaName));
        PathValidator.ValidateNameSegment(objectName, nameof(objectName));

        var sessionDir = GetSessionDirectory(sessionId);
        var objectsDir = Path.Combine(sessionDir, "objects");

        // Search for the file matching schema.name.*.json pattern
        if (!Directory.Exists(objectsDir))
            return null;

        var pattern = $"{schemaName}.{objectName}.*.json";
        var files = Directory.GetFiles(objectsDir, pattern);

        if (files.Length == 0)
            return null;

        var filePath = files[0];
        PathValidator.ValidateResolvedPath(filePath, _baseDirectory);

        var json = await File.ReadAllTextAsync(filePath, ct).ConfigureAwait(false);
        return JsonSerializer.Deserialize<ConversionSessionEntry>(json, SerializerOptions);
    }

    public async Task<IReadOnlyList<ConversionSessionEntry>> GetAllEntriesAsync(
        string sessionId, CancellationToken ct)
    {
        PathValidator.ValidateSessionId(sessionId);

        var sessionDir = GetSessionDirectory(sessionId);
        var objectsDir = Path.Combine(sessionDir, "objects");

        if (!Directory.Exists(objectsDir))
            return [];

        var files = Directory.GetFiles(objectsDir, "*.json");
        var entries = new List<ConversionSessionEntry>(files.Length);

        foreach (var file in files)
        {
            PathValidator.ValidateResolvedPath(file, _baseDirectory);
            var json = await File.ReadAllTextAsync(file, ct).ConfigureAwait(false);
            var entry = JsonSerializer.Deserialize<ConversionSessionEntry>(json, SerializerOptions);
            if (entry is not null)
                entries.Add(entry);
        }

        return entries;
    }

    private string GetSessionDirectory(string sessionId)
    {
        var dir = Path.Combine(_baseDirectory, sessionId);
        PathValidator.ValidateResolvedPath(dir, _baseDirectory);
        return dir;
    }

    private static string GetObjectFileName(string schemaName, string objectName, SchemaObjectType objectType)
    {
        return $"{schemaName}.{objectName}.{objectType}.json";
    }

    private async Task UpdateSessionMetadataAsync(string sessionId, CancellationToken ct)
    {
        await _sessionMetadataLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var sessionDir = GetSessionDirectory(sessionId);
            var sessionFile = Path.Combine(sessionDir, "session.json");

            ConversionSession session;
            if (File.Exists(sessionFile))
            {
                var existingJson = await ReadFileWithRetryAsync(sessionFile, ct).ConfigureAwait(false);
                session = JsonSerializer.Deserialize<ConversionSession>(existingJson, SerializerOptions)
                          ?? throw new InvalidOperationException($"Failed to deserialize session metadata for {sessionId}");
            }
            else
            {
                session = new ConversionSession
                {
                    SessionId = sessionId,
                    CreatedAt = DateTimeOffset.UtcNow,
                    LastModifiedAt = DateTimeOffset.UtcNow
                };
            }

            var objectsDir = Path.Combine(sessionDir, "objects");
            var objectCount = Directory.Exists(objectsDir) ? Directory.GetFiles(objectsDir, "*.json").Length : 0;

            var updated = session with
            {
                LastModifiedAt = DateTimeOffset.UtcNow,
                TotalObjectCount = objectCount
            };

            var json = JsonSerializer.Serialize(updated, SerializerOptions);
            await WriteFileWithRetryAsync(sessionFile, json, ct).ConfigureAwait(false);
        }
        finally
        {
            _sessionMetadataLock.Release();
        }
    }

    /// <summary>
    /// Reads a file with retry logic to handle transient file lock contention on Windows
    /// (e.g., antivirus, indexing services, or delayed handle release).
    /// </summary>
    private static async Task<string> ReadFileWithRetryAsync(
        string path, CancellationToken ct, int maxRetries = 5, int baseDelayMs = 50)
    {
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                return await File.ReadAllTextAsync(path, ct).ConfigureAwait(false);
            }
            catch (IOException) when (attempt < maxRetries)
            {
                await Task.Delay(baseDelayMs * (attempt + 1), ct).ConfigureAwait(false);
            }
        }
    }

    /// <summary>
    /// Writes a file with retry logic to handle transient file lock contention on Windows.
    /// </summary>
    private static async Task WriteFileWithRetryAsync(
        string path, string content, CancellationToken ct, int maxRetries = 5, int baseDelayMs = 50)
    {
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                await File.WriteAllTextAsync(path, content, ct).ConfigureAwait(false);
                return;
            }
            catch (IOException) when (attempt < maxRetries)
            {
                await Task.Delay(baseDelayMs * (attempt + 1), ct).ConfigureAwait(false);
            }
        }
    }
}
