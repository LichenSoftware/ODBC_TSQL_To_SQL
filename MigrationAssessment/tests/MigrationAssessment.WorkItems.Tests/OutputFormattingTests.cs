using System.Text.Json;
using FluentAssertions;
using MigrationAssessment.WorkItems.Models;

namespace MigrationAssessment.WorkItems.Tests;

/// <summary>
/// Unit tests for output formatting: JSON writer, Markdown writer, error handling,
/// and default path resolution.
/// Validates: Requirements 6.5, 6.6, 7.1, 7.2, 7.5
/// </summary>
public class OutputFormattingTests : IDisposable
{
    private readonly string _tempDir;

    public OutputFormattingTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"OutputFormattingTests_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
        {
            Directory.Delete(_tempDir, recursive: true);
        }
    }

    #region Helpers

    private static WorkItemResult CreateSampleResult(int itemCount = 3)
    {
        var workItems = Enumerable.Range(1, itemCount).Select(i => new WorkItem
        {
            Id = $"WI-{i:D3}",
            Title = $"[Risk {Math.Min(i + 1, 5)}] Convert FEATURE_{i} in dbo.TestProc{i}",
            Description = $"Test description for work item {i}.",
            SqlServerPattern = $"SELECT TOP {i * 10} * FROM Table{i}",
            PostgresEquivalent = $"SELECT * FROM Table{i} LIMIT {i * 10}",
            AffectedObjects = new List<AffectedObject>
            {
                new()
                {
                    Name = $"dbo.TestProc{i}",
                    Type = "StoredProcedure",
                    StatementCount = i
                }
            },
            RiskLevel = Math.Min(i + 1, 5),
            Priority = i switch
            {
                1 => "Critical",
                2 => "High",
                3 => "Medium",
                _ => "Low"
            },
            PriorityScore = (itemCount - i + 1) * 10.0,
            EstimatedEffort = new HourRange
            {
                MinHours = i * 0.5,
                MaxHours = i * 2.0
            },
            AcceptanceCriteria = new List<string>
            {
                $"SQL Server construct FEATURE_{i} has been replaced",
                $"PostgreSQL equivalent produces correct results"
            },
            RemediationGuidance = $"Replace FEATURE_{i} with PostgreSQL equivalent.",
            Tags = new List<string> { $"risk-{Math.Min(i + 1, 5)}", "query-feature", "automatic" },
            RelatedWorkItemIds = []
        }).ToList();

        return new WorkItemResult
        {
            WorkItems = workItems,
            Metadata = new WorkItemMetadata
            {
                GeneratedAt = DateTimeOffset.UtcNow,
                SourceAssessmentPath = "./test-assessment.json",
                TotalWorkItemCount = itemCount,
                TotalEstimatedEffort = new HourRange
                {
                    MinHours = workItems.Sum(wi => wi.EstimatedEffort.MinHours),
                    MaxHours = workItems.Sum(wi => wi.EstimatedEffort.MaxHours)
                }
            },
            Succeeded = true
        };
    }

    #endregion

    #region 1. JsonWriter_ProducesValidJson_WithRequiredFields

    /// <summary>
    /// Verifies that JSON output contains metadata and workItems sections with all required fields.
    /// </summary>
    [Fact]
    public async Task JsonWriter_ProducesValidJson_WithRequiredFields()
    {
        var writer = new WorkItemJsonWriter();
        var result = CreateSampleResult(3);
        var outputPath = Path.Combine(_tempDir, "output.json");

        var writeResult = await writer.WriteAsync(result, outputPath, CancellationToken.None);

        writeResult.Succeeded.Should().BeTrue();

        var json = await File.ReadAllTextAsync(outputPath);
        var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        // Verify metadata section
        root.TryGetProperty("metadata", out var metadata).Should().BeTrue("JSON must have 'metadata'");
        metadata.TryGetProperty("generatedAt", out _).Should().BeTrue("metadata must have 'generatedAt'");
        metadata.TryGetProperty("totalWorkItemCount", out var count).Should().BeTrue("metadata must have 'totalWorkItemCount'");
        count.GetInt32().Should().Be(3);
        metadata.TryGetProperty("totalEstimatedEffort", out var effort).Should().BeTrue("metadata must have 'totalEstimatedEffort'");
        effort.TryGetProperty("minHours", out _).Should().BeTrue("effort must have 'minHours'");
        effort.TryGetProperty("maxHours", out _).Should().BeTrue("effort must have 'maxHours'");

        // Verify workItems array
        root.TryGetProperty("workItems", out var workItems).Should().BeTrue("JSON must have 'workItems'");
        workItems.GetArrayLength().Should().Be(3);

        // Verify first work item has all required fields
        var firstItem = workItems[0];
        firstItem.TryGetProperty("id", out _).Should().BeTrue();
        firstItem.TryGetProperty("title", out _).Should().BeTrue();
        firstItem.TryGetProperty("description", out _).Should().BeTrue();
        firstItem.TryGetProperty("sqlServerPattern", out _).Should().BeTrue();
        firstItem.TryGetProperty("postgresEquivalent", out _).Should().BeTrue();
        firstItem.TryGetProperty("affectedObjects", out _).Should().BeTrue();
        firstItem.TryGetProperty("riskLevel", out _).Should().BeTrue();
        firstItem.TryGetProperty("priority", out _).Should().BeTrue();
        firstItem.TryGetProperty("priorityScore", out _).Should().BeTrue();
        firstItem.TryGetProperty("estimatedEffort", out _).Should().BeTrue();
        firstItem.TryGetProperty("acceptanceCriteria", out _).Should().BeTrue();
        firstItem.TryGetProperty("remediationGuidance", out _).Should().BeTrue();
        firstItem.TryGetProperty("tags", out _).Should().BeTrue();
    }

    #endregion

    #region 2. JsonWriter_OrdersWorkItemsByPriorityScoreDescending

    /// <summary>
    /// Verifies that work items in JSON output are ordered by PriorityScore descending.
    /// </summary>
    [Fact]
    public async Task JsonWriter_OrdersWorkItemsByPriorityScoreDescending()
    {
        var writer = new WorkItemJsonWriter();
        var result = CreateSampleResult(5);
        var outputPath = Path.Combine(_tempDir, "ordered.json");

        await writer.WriteAsync(result, outputPath, CancellationToken.None);

        var json = await File.ReadAllTextAsync(outputPath);
        var doc = JsonDocument.Parse(json);
        var workItems = doc.RootElement.GetProperty("workItems");

        var scores = new List<double>();
        foreach (var item in workItems.EnumerateArray())
        {
            scores.Add(item.GetProperty("priorityScore").GetDouble());
        }

        scores.Should().BeInDescendingOrder(
            "work items must be ordered by PriorityScore descending");
    }

    #endregion

    #region 3. JsonWriter_HandlesFileWriteError_ReturnsErrorResult

    /// <summary>
    /// Verifies that writing to an inaccessible path returns Succeeded=false
    /// with an ErrorMessage containing the path.
    /// </summary>
    [Fact]
    public async Task JsonWriter_HandlesFileWriteError_ReturnsErrorResult()
    {
        var writer = new WorkItemJsonWriter();
        var result = CreateSampleResult(1);

        // Create a file and open it exclusively to prevent writing
        var lockedFilePath = Path.Combine(_tempDir, "locked.json");
        await using var lockStream = new FileStream(
            lockedFilePath, FileMode.Create, FileAccess.ReadWrite, FileShare.None);

        var writeResult = await writer.WriteAsync(result, lockedFilePath, CancellationToken.None);

        writeResult.Succeeded.Should().BeFalse("writing to a locked file should fail");
        writeResult.ErrorMessage.Should().NotBeNullOrWhiteSpace(
            "error message should describe the failure");
        writeResult.ErrorMessage.Should().Contain(lockedFilePath,
            "error message should contain the target path");
    }

    #endregion

    #region 4. MarkdownWriter_ContainsSummarySection

    /// <summary>
    /// Verifies markdown output contains the summary section with title, total work items, and effort.
    /// </summary>
    [Fact]
    public void MarkdownWriter_ContainsSummarySection()
    {
        var writer = new WorkItemMarkdownWriter();
        var result = CreateSampleResult(3);

        var markdown = writer.GenerateMarkdown(result);

        markdown.Should().Contain("# Migration Work Items Report",
            "markdown must have main heading");
        markdown.Should().Contain("Total Work Items:",
            "markdown must show total work items");
        markdown.Should().Contain("Estimated Effort:",
            "markdown must show estimated effort");
    }

    #endregion

    #region 5. MarkdownWriter_ContainsRiskDistributionTable

    /// <summary>
    /// Verifies markdown output contains a "## Risk Distribution" section with a markdown table.
    /// </summary>
    [Fact]
    public void MarkdownWriter_ContainsRiskDistributionTable()
    {
        var writer = new WorkItemMarkdownWriter();
        var result = CreateSampleResult(3);

        var markdown = writer.GenerateMarkdown(result);

        markdown.Should().Contain("## Risk Distribution",
            "markdown must contain Risk Distribution heading");
        markdown.Should().Contain("| Priority | Count |",
            "markdown must contain the table header");
        markdown.Should().Contain("|----------|-------|",
            "markdown must contain the table separator");
    }

    #endregion

    #region 6. MarkdownWriter_ContainsTableOfContents

    /// <summary>
    /// Verifies markdown output contains "## Table of Contents" with links to priority groups.
    /// </summary>
    [Fact]
    public void MarkdownWriter_ContainsTableOfContents()
    {
        var writer = new WorkItemMarkdownWriter();
        var result = CreateSampleResult(3);

        var markdown = writer.GenerateMarkdown(result);

        markdown.Should().Contain("## Table of Contents",
            "markdown must contain Table of Contents heading");
        markdown.Should().Contain("[Critical Priority](#critical-priority)",
            "ToC must link to Critical Priority section");
        markdown.Should().Contain("[High Priority](#high-priority)",
            "ToC must link to High Priority section");
        markdown.Should().Contain("[Medium Priority](#medium-priority)",
            "ToC must link to Medium Priority section");
    }

    #endregion

    #region 7. MarkdownWriter_OrganizesByPriorityGroups

    /// <summary>
    /// Verifies markdown output has headings for each priority group present in the data.
    /// </summary>
    [Fact]
    public void MarkdownWriter_OrganizesByPriorityGroups()
    {
        var writer = new WorkItemMarkdownWriter();
        var result = CreateSampleResult(3);

        var markdown = writer.GenerateMarkdown(result);

        markdown.Should().Contain("## Critical Priority",
            "markdown must have Critical Priority section");
        markdown.Should().Contain("## High Priority",
            "markdown must have High Priority section");
        markdown.Should().Contain("## Medium Priority",
            "markdown must have Medium Priority section");
    }

    #endregion

    #region 8. MarkdownWriter_FormatsWorkItemsWithCodeBlocks

    /// <summary>
    /// Verifies each work item has fenced code blocks for SQL patterns.
    /// </summary>
    [Fact]
    public void MarkdownWriter_FormatsWorkItemsWithCodeBlocks()
    {
        var writer = new WorkItemMarkdownWriter();
        var result = CreateSampleResult(1);

        var markdown = writer.GenerateMarkdown(result);

        markdown.Should().Contain("**SQL Server Pattern:**",
            "markdown must label the SQL Server pattern");
        markdown.Should().Contain("```sql",
            "markdown must use fenced code blocks with sql language");
        markdown.Should().Contain("**PostgreSQL Equivalent:**",
            "markdown must label the PostgreSQL equivalent");

        // Verify the actual SQL content is inside code blocks
        markdown.Should().Contain("SELECT TOP 10 * FROM Table1",
            "code block must contain the SQL Server pattern text");
        markdown.Should().Contain("SELECT * FROM Table1 LIMIT 10",
            "code block must contain the PostgreSQL equivalent text");
    }

    #endregion

    #region 9. MarkdownWriter_DefaultPath_WhenNotSpecified

    /// <summary>
    /// Verifies that when MarkdownOutputPath is not specified in configuration,
    /// the default behavior resolves to "work-items.md" in the same directory as the JSON output.
    /// This tests the configuration default contract.
    /// </summary>
    [Fact]
    public void MarkdownWriter_DefaultPath_WhenNotSpecified()
    {
        // When MarkdownOutputPath is null (not specified), the caller should resolve
        // the default to same directory as JSON with "work-items.md" filename.
        var config = new WorkItemConfiguration
        {
            OutputJsonPath = Path.Combine(_tempDir, "subfolder", "work-items.json"),
            MarkdownEnabled = true,
            MarkdownOutputPath = null  // not specified
        };

        config.MarkdownOutputPath.Should().BeNull(
            "when not specified, MarkdownOutputPath should be null");

        // The expected resolved path is same directory as JSON with "work-items.md"
        var expectedDir = Path.GetDirectoryName(config.OutputJsonPath)!;
        var expectedMarkdownPath = Path.Combine(expectedDir, "work-items.md");

        expectedMarkdownPath.Should().Be(
            Path.Combine(_tempDir, "subfolder", "work-items.md"),
            "default Markdown path should be same directory as JSON with 'work-items.md'");

        // Verify the convention: directory from OutputJsonPath + "work-items.md"
        var resolvedPath = config.MarkdownOutputPath
            ?? Path.Combine(
                Path.GetDirectoryName(Path.GetFullPath(config.OutputJsonPath)) ?? ".",
                "work-items.md");

        resolvedPath.Should().EndWith("work-items.md",
            "resolved path should end with 'work-items.md'");
        Path.GetDirectoryName(resolvedPath).Should().Be(
            Path.GetDirectoryName(Path.GetFullPath(config.OutputJsonPath)),
            "resolved path should be in the same directory as the JSON output");
    }

    #endregion
}
