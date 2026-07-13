using Microsoft.Extensions.Logging.Abstractions;
using SchemaConversion.Core.Models;
using SchemaConversion.Reporting;

namespace SchemaConversion.Reporting.Tests;

public class ConversionReportGeneratorTests
{
    private readonly ConversionReportGenerator _generator;

    public ConversionReportGeneratorTests()
    {
        _generator = new ConversionReportGenerator(
            NullLogger<ConversionReportGenerator>.Instance);
    }

    [Fact]
    public async Task GenerateAsync_EmptyEntries_ReturnsReportWithZeroProgress()
    {
        var report = await _generator.GenerateAsync("session-001", [], CancellationToken.None);

        Assert.Equal("session-001", report.SessionId);
        Assert.Equal(0, report.Summary.TotalObjects);
        Assert.Equal(0.0, report.Summary.ProgressPercent);
        Assert.Empty(report.Objects);
        Assert.Empty(report.CompatibilityNotes);
        Assert.Empty(report.FlaggedObjects);
    }

    [Fact]
    public async Task GenerateAsync_AllConverted_Returns100Percent()
    {
        var entries = new List<ConversionSessionEntry>
        {
            CreateEntry("dbo", "Table1", SchemaObjectType.Table, ConversionStatus.Converted, ConversionMethod.RuleBased),
            CreateEntry("dbo", "Table2", SchemaObjectType.Table, ConversionStatus.Converted, ConversionMethod.RuleBased),
            CreateEntry("dbo", "Proc1", SchemaObjectType.StoredProcedure, ConversionStatus.Converted, ConversionMethod.AiAssisted),
        };

        var report = await _generator.GenerateAsync("session-002", entries, CancellationToken.None);

        Assert.Equal(3, report.Summary.TotalObjects);
        Assert.Equal(100.0, report.Summary.ProgressPercent);
    }

    [Fact]
    public async Task GenerateAsync_MixedStatuses_CalculatesCorrectProgress()
    {
        var entries = new List<ConversionSessionEntry>
        {
            CreateEntry("dbo", "Table1", SchemaObjectType.Table, ConversionStatus.Converted, ConversionMethod.RuleBased),
            CreateEntry("dbo", "Proc1", SchemaObjectType.StoredProcedure, ConversionStatus.Flagged, ConversionMethod.AiAssisted),
            CreateEntry("dbo", "Proc2", SchemaObjectType.StoredProcedure, ConversionStatus.Failed, ConversionMethod.AiAssisted),
            CreateEntry("dbo", "Type1", SchemaObjectType.UserDefinedType, ConversionStatus.OutOfScope, ConversionMethod.RuleBased),
            CreateEntry("dbo", "Proc3", SchemaObjectType.StoredProcedure, ConversionStatus.Pending, ConversionMethod.AiAssisted),
        };

        var report = await _generator.GenerateAsync("session-003", entries, CancellationToken.None);

        // Converted + Flagged + OutOfScope = 3 out of 5 = 60%
        Assert.Equal(5, report.Summary.TotalObjects);
        Assert.Equal(60.0, report.Summary.ProgressPercent);
    }

    [Fact]
    public async Task GenerateAsync_GroupsByStatus()
    {
        var entries = new List<ConversionSessionEntry>
        {
            CreateEntry("dbo", "Table1", SchemaObjectType.Table, ConversionStatus.Converted, ConversionMethod.RuleBased),
            CreateEntry("dbo", "Table2", SchemaObjectType.Table, ConversionStatus.Converted, ConversionMethod.RuleBased),
            CreateEntry("dbo", "Proc1", SchemaObjectType.StoredProcedure, ConversionStatus.Flagged, ConversionMethod.AiAssisted),
            CreateEntry("dbo", "Proc2", SchemaObjectType.StoredProcedure, ConversionStatus.Failed, ConversionMethod.AiAssisted),
        };

        var report = await _generator.GenerateAsync("session-004", entries, CancellationToken.None);

        Assert.Equal(2, report.Summary.ByStatus[ConversionStatus.Converted]);
        Assert.Equal(1, report.Summary.ByStatus[ConversionStatus.Flagged]);
        Assert.Equal(1, report.Summary.ByStatus[ConversionStatus.Failed]);
    }

    [Fact]
    public async Task GenerateAsync_GroupsByMethod()
    {
        var entries = new List<ConversionSessionEntry>
        {
            CreateEntry("dbo", "Table1", SchemaObjectType.Table, ConversionStatus.Converted, ConversionMethod.RuleBased),
            CreateEntry("dbo", "Table2", SchemaObjectType.Table, ConversionStatus.Converted, ConversionMethod.RuleBased),
            CreateEntry("dbo", "Proc1", SchemaObjectType.StoredProcedure, ConversionStatus.Converted, ConversionMethod.AiAssisted),
        };

        var report = await _generator.GenerateAsync("session-005", entries, CancellationToken.None);

        Assert.Equal(2, report.Summary.ByMethod[ConversionMethod.RuleBased]);
        Assert.Equal(1, report.Summary.ByMethod[ConversionMethod.AiAssisted]);
    }

    [Fact]
    public async Task GenerateAsync_GroupsByType()
    {
        var entries = new List<ConversionSessionEntry>
        {
            CreateEntry("dbo", "Table1", SchemaObjectType.Table, ConversionStatus.Converted, ConversionMethod.RuleBased),
            CreateEntry("dbo", "Table2", SchemaObjectType.Table, ConversionStatus.Converted, ConversionMethod.RuleBased),
            CreateEntry("dbo", "View1", SchemaObjectType.View, ConversionStatus.Converted, ConversionMethod.RuleBased),
        };

        var report = await _generator.GenerateAsync("session-006", entries, CancellationToken.None);

        Assert.Equal(2, report.Summary.ByType[SchemaObjectType.Table]);
        Assert.Equal(1, report.Summary.ByType[SchemaObjectType.View]);
    }

    [Fact]
    public async Task GenerateAsync_AggregatesCompatibilityNotes_DeduplicatesIdentical()
    {
        var entries = new List<ConversionSessionEntry>
        {
            CreateEntryWithNotes("dbo", "Table1", SchemaObjectType.Table,
                new CompatibilityNote { Category = "NullHandling", Description = "NULL concatenation differs" }),
            CreateEntryWithNotes("dbo", "Table2", SchemaObjectType.Table,
                new CompatibilityNote { Category = "NullHandling", Description = "NULL concatenation differs" }),
            CreateEntryWithNotes("dbo", "Proc1", SchemaObjectType.StoredProcedure,
                new CompatibilityNote { Category = "Locking", Description = "NOLOCK hints removed" }),
        };

        var report = await _generator.GenerateAsync("session-007", entries, CancellationToken.None);

        // Identical notes should be deduplicated
        Assert.Equal(2, report.CompatibilityNotes.Count);
        Assert.Contains(report.CompatibilityNotes, n => n.Category == "NullHandling");
        Assert.Contains(report.CompatibilityNotes, n => n.Category == "Locking");
    }

    [Fact]
    public async Task GenerateAsync_IdentifiesFlaggedObjects()
    {
        var entries = new List<ConversionSessionEntry>
        {
            CreateEntry("dbo", "Table1", SchemaObjectType.Table, ConversionStatus.Converted, ConversionMethod.RuleBased),
            CreateEntry("dbo", "Proc1", SchemaObjectType.StoredProcedure, ConversionStatus.Flagged, ConversionMethod.AiAssisted),
            CreateEntryWithReviewFlags("dbo", "Proc2", SchemaObjectType.StoredProcedure,
                ConversionStatus.Converted, new ManualReviewFlag { Reason = "Low confidence" }),
        };

        var report = await _generator.GenerateAsync("session-008", entries, CancellationToken.None);

        Assert.Equal(2, report.FlaggedObjects.Count);
        Assert.Contains(report.FlaggedObjects, e => e.Source.Name == "Proc1");
        Assert.Contains(report.FlaggedObjects, e => e.Source.Name == "Proc2");
    }

    [Fact]
    public async Task GenerateAsync_SetsGeneratedAtTimestamp()
    {
        var before = DateTimeOffset.UtcNow;

        var report = await _generator.GenerateAsync("session-009", [], CancellationToken.None);

        var after = DateTimeOffset.UtcNow;
        Assert.InRange(report.GeneratedAt, before, after);
    }

    [Fact]
    public async Task GenerateAsync_ManuallyReviewedCountsAsProgress()
    {
        var entries = new List<ConversionSessionEntry>
        {
            CreateEntry("dbo", "Proc1", SchemaObjectType.StoredProcedure, ConversionStatus.ManuallyReviewed, ConversionMethod.Manual),
            CreateEntry("dbo", "Proc2", SchemaObjectType.StoredProcedure, ConversionStatus.Pending, ConversionMethod.AiAssisted),
        };

        var report = await _generator.GenerateAsync("session-010", entries, CancellationToken.None);

        Assert.Equal(50.0, report.Summary.ProgressPercent);
    }

    private static ConversionSessionEntry CreateEntry(
        string schema, string name, SchemaObjectType type,
        ConversionStatus status, ConversionMethod method)
    {
        return new ConversionSessionEntry
        {
            Source = new SchemaObject
            {
                SchemaName = schema,
                Name = name,
                ObjectType = type,
                SourceDefinition = $"CREATE {type} {schema}.{name}",
                SourceDefinitionHash = $"hash-{schema}-{name}"
            },
            Result = new ConversionResult
            {
                ObjectName = name,
                SchemaName = schema,
                ObjectType = type,
                Status = status,
                Method = method,
                GeneratedDdl = $"-- Generated DDL for {schema}.{name}"
            },
            ConvertedAt = DateTimeOffset.UtcNow
        };
    }

    private static ConversionSessionEntry CreateEntryWithNotes(
        string schema, string name, SchemaObjectType type,
        params CompatibilityNote[] notes)
    {
        return new ConversionSessionEntry
        {
            Source = new SchemaObject
            {
                SchemaName = schema,
                Name = name,
                ObjectType = type,
                SourceDefinition = $"CREATE {type} {schema}.{name}",
                SourceDefinitionHash = $"hash-{schema}-{name}"
            },
            Result = new ConversionResult
            {
                ObjectName = name,
                SchemaName = schema,
                ObjectType = type,
                Status = ConversionStatus.Converted,
                Method = ConversionMethod.RuleBased,
                GeneratedDdl = $"-- Generated DDL for {schema}.{name}",
                CompatibilityNotes = notes
            },
            ConvertedAt = DateTimeOffset.UtcNow
        };
    }

    private static ConversionSessionEntry CreateEntryWithReviewFlags(
        string schema, string name, SchemaObjectType type,
        ConversionStatus status, params ManualReviewFlag[] flags)
    {
        return new ConversionSessionEntry
        {
            Source = new SchemaObject
            {
                SchemaName = schema,
                Name = name,
                ObjectType = type,
                SourceDefinition = $"CREATE {type} {schema}.{name}",
                SourceDefinitionHash = $"hash-{schema}-{name}"
            },
            Result = new ConversionResult
            {
                ObjectName = name,
                SchemaName = schema,
                ObjectType = type,
                Status = status,
                Method = ConversionMethod.AiAssisted,
                GeneratedDdl = $"-- Generated DDL for {schema}.{name}",
                ReviewFlags = flags
            },
            ConvertedAt = DateTimeOffset.UtcNow
        };
    }
}
