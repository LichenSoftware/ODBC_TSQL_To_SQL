using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using MigrationAssessment.Cli;
using MigrationAssessment.Core.Models;
using MigrationAssessment.WorkItems;
using MigrationAssessment.WorkItems.Models;

namespace MigrationAssessment.WorkItems.Tests;

/// <summary>
/// Unit tests for CLI argument parsing and pipeline integration.
/// Validates: Requirements 10.1, 10.4, 10.5, 9.6
/// </summary>
public class CliAndPipelineIntegrationTests : IDisposable
{
    private readonly List<string> _tempFiles = new();

    private static string GetTestAssessmentPath()
    {
        // Walk up from bin/Debug/net8.0 to MigrationAssessment root
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "test-assessment.json")))
        {
            dir = dir.Parent;
        }
        return dir != null
            ? Path.Combine(dir.FullName, "test-assessment.json")
            : throw new FileNotFoundException("test-assessment.json not found walking up from " + AppContext.BaseDirectory);
    }

    private string GetTempFilePath(string extension)
    {
        var path = Path.Combine(Path.GetTempPath(), $"cli-test-{Guid.NewGuid():N}{extension}");
        _tempFiles.Add(path);
        return path;
    }

    public void Dispose()
    {
        foreach (var file in _tempFiles)
        {
            if (File.Exists(file))
            {
                try { File.Delete(file); } catch { /* best effort cleanup */ }
            }
        }
    }

    // ─── CLI Tests ───────────────────────────────────────────────────────────────

    [Fact]
    public async Task GenerateWorkItemsCommand_NoArgs_ReturnsExitCode1()
    {
        var exitCode = await GenerateWorkItemsCommand.RunAsync([], CancellationToken.None);

        exitCode.Should().Be(1, "missing required input path should show usage and return exit code 1");
    }

    [Fact]
    public async Task GenerateWorkItemsCommand_WithInputPath_ParsesCorrectly()
    {
        var testAssessmentPath = GetTestAssessmentPath();
        var outputPath = GetTempFilePath(".json");

        var exitCode = await GenerateWorkItemsCommand.RunAsync(
            [testAssessmentPath, "--output", outputPath],
            CancellationToken.None);

        exitCode.Should().Be(0, "a valid input path with output redirect should succeed");
    }

    [Fact]
    public async Task GenerateWorkItemsCommand_AllOptions_ParsesCorrectly()
    {
        var testAssessmentPath = GetTestAssessmentPath();
        var outputJsonPath = GetTempFilePath(".json");
        var outputMdPath = GetTempFilePath(".md");

        var exitCode = await GenerateWorkItemsCommand.RunAsync(
            [testAssessmentPath, "--output", outputJsonPath, "--markdown", "--markdown-output", outputMdPath, "--min-risk", "3", "--max-items", "5"],
            CancellationToken.None);

        exitCode.Should().Be(0, "all valid options should parse and execute successfully");
    }

    // ─── WorkItemGeneratorService Validation Tests ───────────────────────────────

    private static WorkItemGeneratorService CreateRealGenerator()
    {
        var grouper = new StatementGrouper(NullLogger<StatementGrouper>.Instance);
        var priorityCalculator = new PriorityCalculator();
        var effortEstimator = new EffortEstimator();
        var knowledgeBase = new RemediationKnowledgeBase();
        var conversionEngine = new PostgresConversionEngine();
        var deduplicator = new WorkItemDeduplicator();
        var titleGenerator = new TitleGenerator();
        var descriptionGenerator = new DescriptionGenerator();
        var guidanceGenerator = new RemediationGuidanceGenerator(knowledgeBase);
        var acceptanceCriteriaGenerator = new AcceptanceCriteriaGenerator();
        var jsonReader = new AssessmentJsonReader();
        var jsonWriter = new WorkItemJsonWriter();
        var markdownWriter = new WorkItemMarkdownWriter();

        return new WorkItemGeneratorService(
            grouper, priorityCalculator, effortEstimator, knowledgeBase, conversionEngine,
            deduplicator, titleGenerator, descriptionGenerator, guidanceGenerator,
            acceptanceCriteriaGenerator, jsonReader, jsonWriter, markdownWriter);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(6)]
    [InlineData(-1)]
    public void WorkItemGeneratorService_InvalidMinRiskLevel_ReturnsError(int invalidRiskLevel)
    {
        var generator = CreateRealGenerator();
        var config = new WorkItemConfiguration { MinimumRiskLevel = invalidRiskLevel };
        var statements = Array.Empty<AnalyzedStatement>();
        var featureDetection = new FeatureDetectionResult
        {
            FeatureCounts = new Dictionary<string, int>(),
            DetailedInventory = [],
            InaccessibleFeatures = []
        };

        var result = generator.GenerateWorkItems(statements, featureDetection, config);

        result.Succeeded.Should().BeFalse();
        result.ErrorMessage.Should().Contain("MinimumRiskLevel");
        result.ErrorMessage.Should().Contain(invalidRiskLevel.ToString());
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(-100)]
    public void WorkItemGeneratorService_InvalidMaxCount_ReturnsError(int invalidMaxCount)
    {
        var generator = CreateRealGenerator();
        var config = new WorkItemConfiguration { MaxWorkItemCount = invalidMaxCount };
        var statements = Array.Empty<AnalyzedStatement>();
        var featureDetection = new FeatureDetectionResult
        {
            FeatureCounts = new Dictionary<string, int>(),
            DetailedInventory = [],
            InaccessibleFeatures = []
        };

        var result = generator.GenerateWorkItems(statements, featureDetection, config);

        result.Succeeded.Should().BeFalse();
        result.ErrorMessage.Should().Contain("MaxWorkItemCount");
        result.ErrorMessage.Should().Contain(invalidMaxCount.ToString());
    }

    [Fact]
    public void WorkItemGeneratorService_ValidConfig_ProducesWorkItems()
    {
        var generator = CreateRealGenerator();
        var config = new WorkItemConfiguration();

        var statements = new List<AnalyzedStatement>
        {
            new AnalyzedStatement
            {
                Source = new CollectedStatement
                {
                    SqlText = "SELECT TOP 10 * FROM Orders",
                    Source = StatementSource.QueryStore,
                    QueryHash = "hash1",
                    ExecutionCount = 100
                },
                Classification = StatementClassification.Select,
                Features = new[]
                {
                    new DetectedFeature
                    {
                        FeatureName = "TOP",
                        Category = FeatureCategory.QueryFeature,
                        StatementId = "stmt-1",
                        Line = 1,
                        Column = 1
                    }
                },
                RiskScore = 2,
                WeightedRisk = 200.0,
                ParseSucceeded = true,
                AnalysisComplete = true
            }
        };

        var featureDetection = new FeatureDetectionResult
        {
            FeatureCounts = new Dictionary<string, int>(),
            DetailedInventory = [],
            InaccessibleFeatures = []
        };

        var result = generator.GenerateWorkItems(statements, featureDetection, config);

        result.Succeeded.Should().BeTrue();
        result.WorkItems.Should().NotBeEmpty();
        result.WorkItems.All(wi => wi.RiskLevel >= 1).Should().BeTrue();
    }

    // ─── Pipeline Integration Test ──────────────────────────────────────────────

    [Fact]
    public void PipelineIntegration_GenerateWorkItemsDisabled_SkipsGeneration()
    {
        // When no statements are provided (simulating disabled/skipped generation),
        // the generator produces an empty result with success.
        var generator = CreateRealGenerator();
        var config = new WorkItemConfiguration();
        var emptyStatements = Array.Empty<AnalyzedStatement>();
        var featureDetection = new FeatureDetectionResult
        {
            FeatureCounts = new Dictionary<string, int>(),
            DetailedInventory = [],
            InaccessibleFeatures = []
        };

        var result = generator.GenerateWorkItems(emptyStatements, featureDetection, config);

        result.Succeeded.Should().BeTrue();
        result.WorkItems.Should().BeEmpty("no statements means no work items generated");
        result.Metadata.TotalWorkItemCount.Should().Be(0);
    }
}
