using Microsoft.Extensions.Logging.Abstractions;
using SchemaConversion.Core.Models;
using SchemaConversion.Orchestration;

namespace SchemaConversion.Orchestration.Tests;

public sealed class ConversionSessionStoreTests : IDisposable
{
    private readonly string _testDir;
    private readonly ConversionSessionStore _store;

    public ConversionSessionStoreTests()
    {
        _testDir = Path.Combine(Path.GetTempPath(), "session-tests-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_testDir);
        _store = new ConversionSessionStore(_testDir, NullLogger<ConversionSessionStore>.Instance);
    }

    [Fact]
    public async Task LoadOrCreateAsync_CreatesNewSession_WhenNotExists()
    {
        var session = await _store.LoadOrCreateAsync("test-session-01", CancellationToken.None);

        Assert.Equal("test-session-01", session.SessionId);
        Assert.Equal(0, session.TotalObjectCount);
        Assert.True(Directory.Exists(Path.Combine(_testDir, "test-session-01", "objects")));
        Assert.True(Directory.Exists(Path.Combine(_testDir, "test-session-01", "audit")));
    }

    [Fact]
    public async Task LoadOrCreateAsync_LoadsExistingSession()
    {
        // Create first
        var original = await _store.LoadOrCreateAsync("test-session-02", CancellationToken.None);
        // Load again
        var loaded = await _store.LoadOrCreateAsync("test-session-02", CancellationToken.None);

        Assert.Equal(original.SessionId, loaded.SessionId);
        Assert.Equal(original.CreatedAt, loaded.CreatedAt);
    }

    [Fact]
    public async Task SaveAndGetEntry_RoundTrip()
    {
        const string sessionId = "round-trip-test";
        await _store.LoadOrCreateAsync(sessionId, CancellationToken.None);

        var entry = CreateTestEntry("dbo", "Customers", SchemaObjectType.Table);

        await _store.SaveEntryAsync(sessionId, entry, CancellationToken.None);

        var loaded = await _store.GetEntryAsync(sessionId, "dbo", "Customers", CancellationToken.None);

        Assert.NotNull(loaded);
        Assert.Equal(entry.Source.Name, loaded.Source.Name);
        Assert.Equal(entry.Source.SchemaName, loaded.Source.SchemaName);
        Assert.Equal(entry.Source.ObjectType, loaded.Source.ObjectType);
        Assert.Equal(entry.Source.SourceDefinition, loaded.Source.SourceDefinition);
        Assert.Equal(entry.Source.SourceDefinitionHash, loaded.Source.SourceDefinitionHash);
        Assert.Equal(entry.Result.Status, loaded.Result.Status);
        Assert.Equal(entry.Result.Method, loaded.Result.Method);
        Assert.Equal(entry.Result.GeneratedDdl, loaded.Result.GeneratedDdl);
        Assert.Equal(entry.IsManuallyEdited, loaded.IsManuallyEdited);
    }

    [Fact]
    public async Task GetAllEntriesAsync_ReturnsAllSavedEntries()
    {
        const string sessionId = "get-all-test";
        await _store.LoadOrCreateAsync(sessionId, CancellationToken.None);

        var entry1 = CreateTestEntry("dbo", "Customers", SchemaObjectType.Table);
        var entry2 = CreateTestEntry("dbo", "GetOrders", SchemaObjectType.StoredProcedure);
        var entry3 = CreateTestEntry("sales", "OrderTotal", SchemaObjectType.Function);

        await _store.SaveEntryAsync(sessionId, entry1, CancellationToken.None);
        await _store.SaveEntryAsync(sessionId, entry2, CancellationToken.None);
        await _store.SaveEntryAsync(sessionId, entry3, CancellationToken.None);

        var all = await _store.GetAllEntriesAsync(sessionId, CancellationToken.None);

        Assert.Equal(3, all.Count);
    }

    [Fact]
    public async Task GetEntryAsync_ReturnsNull_WhenNotExists()
    {
        const string sessionId = "missing-entry-test";
        await _store.LoadOrCreateAsync(sessionId, CancellationToken.None);

        var result = await _store.GetEntryAsync(sessionId, "dbo", "NonExistent", CancellationToken.None);

        Assert.Null(result);
    }

    [Fact]
    public async Task SaveEntryAsync_UpdatesSessionMetadata()
    {
        const string sessionId = "metadata-update-test";
        await _store.LoadOrCreateAsync(sessionId, CancellationToken.None);

        var entry = CreateTestEntry("dbo", "Customers", SchemaObjectType.Table);
        await _store.SaveEntryAsync(sessionId, entry, CancellationToken.None);

        var reloaded = await _store.LoadOrCreateAsync(sessionId, CancellationToken.None);
        Assert.Equal(1, reloaded.TotalObjectCount);
    }

    [Theory]
    [InlineData("../escape")]
    [InlineData("..\\escape")]
    [InlineData("bad/path")]
    [InlineData("bad\\path")]
    [InlineData("bad path")]
    [InlineData("bad@path")]
    public async Task LoadOrCreateAsync_RejectsInvalidSessionIds(string invalidId)
    {
        await Assert.ThrowsAsync<ArgumentException>(
            () => _store.LoadOrCreateAsync(invalidId, CancellationToken.None));
    }

    [Theory]
    [InlineData("..")]
    [InlineData("../schema")]
    [InlineData("sche ma")]
    public async Task GetEntryAsync_RejectsInvalidNames(string invalidName)
    {
        const string sessionId = "path-traversal-test";
        await _store.LoadOrCreateAsync(sessionId, CancellationToken.None);

        await Assert.ThrowsAsync<ArgumentException>(
            () => _store.GetEntryAsync(sessionId, invalidName, "obj", CancellationToken.None));
    }

    private static ConversionSessionEntry CreateTestEntry(
        string schema, string name, SchemaObjectType type)
    {
        return new ConversionSessionEntry
        {
            Source = new SchemaObject
            {
                Name = name,
                SchemaName = schema,
                ObjectType = type,
                SourceDefinition = $"CREATE {type} [{schema}].[{name}] ...",
                SourceDefinitionHash = "abc123def456",
                DependsOn = []
            },
            Result = new ConversionResult
            {
                ObjectName = name,
                SchemaName = schema,
                ObjectType = type,
                Status = ConversionStatus.Converted,
                Method = ConversionMethod.RuleBased,
                GeneratedDdl = $"CREATE {type} {schema}.{name} ...",
            },
            ConvertedAt = DateTimeOffset.UtcNow,
            IsManuallyEdited = false
        };
    }

    public void Dispose()
    {
        if (Directory.Exists(_testDir))
            Directory.Delete(_testDir, recursive: true);
    }
}
