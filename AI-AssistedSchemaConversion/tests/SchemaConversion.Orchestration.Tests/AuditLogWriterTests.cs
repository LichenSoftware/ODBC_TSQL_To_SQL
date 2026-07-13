using Microsoft.Extensions.Logging.Abstractions;
using SchemaConversion.Core.Models;
using SchemaConversion.Orchestration;

namespace SchemaConversion.Orchestration.Tests;

public sealed class AuditLogWriterTests : IDisposable
{
    private readonly string _testDir;

    public AuditLogWriterTests()
    {
        _testDir = Path.Combine(Path.GetTempPath(), "audit-tests-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_testDir);
    }

    [Fact]
    public async Task WriteAsync_CreatesAuditFile()
    {
        using var writer = new AuditLogWriter(_testDir, NullLogger<AuditLogWriter>.Instance);

        var entry = CreateTestAuditEntry("TestProc");
        await writer.WriteAsync(entry, CancellationToken.None);

        var files = Directory.GetFiles(_testDir, "audit-*.jsonl");
        Assert.Single(files);
        Assert.Contains("audit-001.jsonl", files[0]);
    }

    [Fact]
    public async Task WriteAsync_AppendsMultipleEntries()
    {
        using var writer = new AuditLogWriter(_testDir, NullLogger<AuditLogWriter>.Instance);

        await writer.WriteAsync(CreateTestAuditEntry("Proc1"), CancellationToken.None);
        await writer.WriteAsync(CreateTestAuditEntry("Proc2"), CancellationToken.None);
        await writer.WriteAsync(CreateTestAuditEntry("Proc3"), CancellationToken.None);

        var file = Directory.GetFiles(_testDir, "audit-*.jsonl")[0];
        var lines = await File.ReadAllLinesAsync(file);

        // Filter out empty lines
        var nonEmptyLines = lines.Where(l => !string.IsNullOrWhiteSpace(l)).ToArray();
        Assert.Equal(3, nonEmptyLines.Length);
    }

    [Fact]
    public async Task WriteAsync_RotatesFileWhenMaxSizeExceeded()
    {
        // Use a small max size to trigger rotation
        using var writer = new AuditLogWriter(_testDir, NullLogger<AuditLogWriter>.Instance, maxFileSizeBytes: 100);

        // Write enough entries to trigger rotation
        await writer.WriteAsync(CreateTestAuditEntry("LargeProc1"), CancellationToken.None);
        await writer.WriteAsync(CreateTestAuditEntry("LargeProc2"), CancellationToken.None);

        var files = Directory.GetFiles(_testDir, "audit-*.jsonl");
        Assert.True(files.Length >= 2, $"Expected at least 2 files after rotation, got {files.Length}");
    }

    [Fact]
    public async Task WriteAsync_UsesUtcTimestampWithMsPrecision()
    {
        using var writer = new AuditLogWriter(_testDir, NullLogger<AuditLogWriter>.Instance);

        var entry = CreateTestAuditEntry("TestProc");
        await writer.WriteAsync(entry, CancellationToken.None);

        var file = Directory.GetFiles(_testDir, "audit-*.jsonl")[0];
        var content = await File.ReadAllTextAsync(file);

        // UTC timestamp pattern: "2026-07-04T12:15:28.123Z"
        Assert.Matches(@"\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}\.\d{3}Z", content);
    }

    [Fact]
    public async Task WriteAsync_DoesNotContainSensitiveData()
    {
        using var writer = new AuditLogWriter(_testDir, NullLogger<AuditLogWriter>.Instance);

        var entry = CreateTestAuditEntry("TestProc");
        await writer.WriteAsync(entry, CancellationToken.None);

        var file = Directory.GetFiles(_testDir, "audit-*.jsonl")[0];
        var content = await File.ReadAllTextAsync(file);

        // Should not contain connection string patterns
        Assert.DoesNotContain("password", content, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("connectionstring", content, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Constructor_RejectsInvalidArguments()
    {
        Assert.Throws<ArgumentException>(() =>
            new AuditLogWriter("", NullLogger<AuditLogWriter>.Instance));

        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new AuditLogWriter(_testDir, NullLogger<AuditLogWriter>.Instance, maxFileSizeBytes: 0));

        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new AuditLogWriter(_testDir, NullLogger<AuditLogWriter>.Instance, maxFileSizeBytes: -1));
    }

    private static AuditLogEntry CreateTestAuditEntry(string objectName)
    {
        return new AuditLogEntry
        {
            SessionId = "test-session",
            ObjectName = objectName,
            ObjectType = SchemaObjectType.StoredProcedure,
            PromptTemplateVersion = "1.0.0",
            FullPrompt = "Convert this stored procedure",
            ModelId = "anthropic.claude-sonnet-4-20250514-v1:0",
            FullResponse = "{\"ddl\": \"CREATE FUNCTION ...\"}",
            Timestamp = DateTimeOffset.UtcNow,
            RetryAttempt = 0,
            IsError = false
        };
    }

    public void Dispose()
    {
        if (Directory.Exists(_testDir))
            Directory.Delete(_testDir, recursive: true);
    }
}
