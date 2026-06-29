using FluentAssertions;
using MigrationAssessment.Core.Models;
using MigrationAssessment.WorkItems.Models;
using HourRange = MigrationAssessment.WorkItems.Models.HourRange;

namespace MigrationAssessment.WorkItems.Tests;

/// <summary>
/// Tests for the WorkItemValidator validation gate.
/// Verifies that the validator catches invalid SQL, object attribution mismatches,
/// and effort range violations, and that valid work items produce zero warnings.
/// </summary>
public class WorkItemValidatorTests
{
    private readonly WorkItemValidator _validator = new();

    #region SQL Syntax Validation

    [Fact]
    public void Validate_ValidPostgresEquivalent_NoSqlWarnings()
    {
        var workItems = new[]
        {
            CreateWorkItem("WI-001", "SELECT COALESCE(a, 0) FROM t\nLIMIT 10", ConfidenceLevel.High)
        };

        var result = _validator.Validate(workItems, null);

        result.Warnings.Should().NotContain(w => w.Category == "sql-syntax");
    }

    [Fact]
    public void Validate_UnbalancedParentheses_FlagsSqlWarning()
    {
        var workItems = new[]
        {
            CreateWorkItem("WI-001", "SELECT COALESCE(a, 0 FROM t", ConfidenceLevel.High)
        };

        var result = _validator.Validate(workItems, null);

        result.Passed.Should().BeFalse();
        result.Warnings.Should().ContainSingle(w =>
            w.WorkItemId == "WI-001" && w.Category == "sql-syntax");
    }

    [Fact]
    public void Validate_EmptyPostgresEquivalent_FlagsSqlWarning()
    {
        var workItems = new[]
        {
            CreateWorkItem("WI-001", "", ConfidenceLevel.High)
        };

        var result = _validator.Validate(workItems, null);

        result.Passed.Should().BeFalse();
        result.Warnings.Should().Contain(w =>
            w.WorkItemId == "WI-001" && w.Category == "sql-syntax");
    }

    [Fact]
    public void Validate_CommentOnlyOutput_NoSqlWarning()
    {
        var workItems = new[]
        {
            CreateWorkItem("WI-001", "-- TODO: manual conversion required\n-- Original: complex SQL", ConfidenceLevel.Low)
        };

        var result = _validator.Validate(workItems, null);

        result.Warnings.Should().NotContain(w => w.Category == "sql-syntax");
    }

    [Fact]
    public void Validate_ForUpdateOnUpdateStatement_FlagsSqlWarning()
    {
        var workItems = new[]
        {
            CreateWorkItem("WI-001", "UPDATE t SET x = 1 WHERE id = 1\nFOR UPDATE", ConfidenceLevel.Low)
        };

        var result = _validator.Validate(workItems, null);

        result.Passed.Should().BeFalse();
        result.Warnings.Should().Contain(w =>
            w.WorkItemId == "WI-001" && w.Category == "sql-syntax");
    }

    [Fact]
    public void Validate_RevertedUpdlockBug_CaughtByValidator()
    {
        // Simulates the old broken behavior where FOR UPDATE was appended to an UPDATE statement
        // This test proves the validator catches the bug that TASK-09 fixed
        var brokenSql = "-- TODO: verify locking strategy\nUPDATE Products SET Qty = Qty - 1 WHERE Id = 1\nFOR UPDATE";

        var workItems = new[]
        {
            CreateWorkItem("WI-002", brokenSql, ConfidenceLevel.Low)
        };

        var result = _validator.Validate(workItems, null);

        result.Passed.Should().BeFalse("validator should catch FOR UPDATE on UPDATE statement");
        result.Warnings.Should().Contain(w =>
            w.WorkItemId == "WI-002" && w.Category == "sql-syntax");
    }

    #endregion

    #region Object Attribution Consistency

    [Fact]
    public void Validate_AffectedObjectExistsInInventory_NoAttributionWarning()
    {
        var workItems = new[]
        {
            CreateWorkItemWithObject("WI-001", "sp_Test", "StoredProcedure", new[] { "NOLOCK" })
        };

        var inventory = new[]
        {
            new ObjectInventoryEntry
            {
                Name = "sp_Test",
                Type = "StoredProcedure",
                StatementCount = 3,
                MaxRiskScore = 4,
                ConversionCategories = new[] { "manual" },
                DetectedFeatures = new[] { "NOLOCK", "UPDLOCK" }
            }
        };

        var result = _validator.Validate(workItems, inventory);

        result.Warnings.Should().NotContain(w => w.Category == "object-attribution");
    }

    [Fact]
    public void Validate_AffectedObjectNotInInventory_FlagsWarning()
    {
        var workItems = new[]
        {
            CreateWorkItemWithObject("WI-001", "sp_NonExistent", "StoredProcedure", new[] { "NOLOCK" })
        };

        var inventory = new[]
        {
            new ObjectInventoryEntry
            {
                Name = "sp_Other",
                Type = "StoredProcedure",
                StatementCount = 1,
                MaxRiskScore = 2,
                ConversionCategories = new[] { "automatic" },
                DetectedFeatures = new[] { "TOP" }
            }
        };

        var result = _validator.Validate(workItems, inventory);

        result.Passed.Should().BeFalse();
        result.Warnings.Should().Contain(w =>
            w.WorkItemId == "WI-001" &&
            w.Category == "object-attribution" &&
            w.Message.Contains("sp_NonExistent"));
    }

    [Fact]
    public void Validate_FeatureMismatchBetweenWorkItemAndInventory_FlagsWarning()
    {
        var workItems = new[]
        {
            CreateWorkItemWithObject("WI-001", "sp_Test", "StoredProcedure", new[] { "MERGE" })
        };

        var inventory = new[]
        {
            new ObjectInventoryEntry
            {
                Name = "sp_Test",
                Type = "StoredProcedure",
                StatementCount = 3,
                MaxRiskScore = 2,
                ConversionCategories = new[] { "automatic" },
                DetectedFeatures = new[] { "TOP", "ISNULL" } // No MERGE!
            }
        };

        var result = _validator.Validate(workItems, inventory);

        result.Passed.Should().BeFalse();
        result.Warnings.Should().Contain(w =>
            w.WorkItemId == "WI-001" &&
            w.Category == "object-attribution" &&
            w.Message.Contains("MERGE"));
    }

    [Fact]
    public void Validate_AdHocQueriesWithNoAdHocEntryInInventory_FlagsWarning()
    {
        var workItems = new[]
        {
            CreateWorkItemWithObject("WI-001", "Ad Hoc Queries", "AdHoc", new[] { "TOP" })
        };

        // Inventory has named objects but no AdHoc entry
        var inventory = new[]
        {
            new ObjectInventoryEntry
            {
                Name = "sp_Test",
                Type = "StoredProcedure",
                StatementCount = 1,
                MaxRiskScore = 2,
                ConversionCategories = new[] { "automatic" },
                DetectedFeatures = new[] { "TOP" }
            }
        };

        var result = _validator.Validate(workItems, inventory);

        result.Passed.Should().BeFalse();
        result.Warnings.Should().Contain(w =>
            w.Category == "object-attribution" &&
            w.Message.Contains("Ad Hoc"));
    }

    #endregion

    #region Effort Range Sanity

    [Fact]
    public void Validate_ValidEffortRange_NoWarning()
    {
        var workItems = new[]
        {
            CreateWorkItem("WI-001", "SELECT 1", ConfidenceLevel.High, minHours: 1.0, maxHours: 1.4)
        };

        var result = _validator.Validate(workItems, null);

        result.Warnings.Should().NotContain(w => w.Category == "effort-range");
    }

    [Fact]
    public void Validate_MaxLessThanMin_FlagsWarning()
    {
        var workItems = new[]
        {
            CreateWorkItem("WI-001", "SELECT 1", ConfidenceLevel.High, minHours: 5.0, maxHours: 2.0)
        };

        var result = _validator.Validate(workItems, null);

        result.Passed.Should().BeFalse();
        result.Warnings.Should().Contain(w =>
            w.WorkItemId == "WI-001" && w.Category == "effort-range");
    }

    [Fact]
    public void Validate_HighConfidence_RatioExceeds2x_FlagsWarning()
    {
        // High confidence allows ≤1.5x ratio. 1.0 to 2.0 is 2.0x → should flag
        var workItems = new[]
        {
            CreateWorkItem("WI-001", "SELECT 1", ConfidenceLevel.High, minHours: 1.0, maxHours: 2.0)
        };

        var result = _validator.Validate(workItems, null);

        result.Passed.Should().BeFalse();
        result.Warnings.Should().Contain(w =>
            w.WorkItemId == "WI-001" &&
            w.Category == "effort-range" &&
            w.Message.Contains("1.5x"));
    }

    [Fact]
    public void Validate_MediumConfidence_RatioWithin4x_NoWarning()
    {
        // Medium confidence allows ≤2x ratio. 1.0 to 1.8 is 1.8x → OK
        var workItems = new[]
        {
            CreateWorkItem("WI-001", "SELECT 1", ConfidenceLevel.Medium, minHours: 1.0, maxHours: 1.8)
        };

        var result = _validator.Validate(workItems, null);

        result.Warnings.Should().NotContain(w => w.Category == "effort-range");
    }

    [Fact]
    public void Validate_MediumConfidence_RatioExceeds4x_FlagsWarning()
    {
        // Medium confidence allows ≤2x. 1.0 to 2.5 is 2.5x → should flag
        var workItems = new[]
        {
            CreateWorkItem("WI-001", "SELECT 1", ConfidenceLevel.Medium, minHours: 1.0, maxHours: 2.5)
        };

        var result = _validator.Validate(workItems, null);

        result.Passed.Should().BeFalse();
        result.Warnings.Should().Contain(w =>
            w.Category == "effort-range" && w.Message.Contains("2.0x"));
    }

    [Fact]
    public void Validate_LowConfidence_RatioWithin7x_NoWarning()
    {
        // Low confidence allows ≤3x. 1.0 to 2.8 is 2.8x → OK
        var workItems = new[]
        {
            CreateWorkItem("WI-001", "SELECT 1", ConfidenceLevel.Low, minHours: 1.0, maxHours: 2.8)
        };

        var result = _validator.Validate(workItems, null);

        result.Warnings.Should().NotContain(w => w.Category == "effort-range");
    }

    [Fact]
    public void Validate_LowConfidence_RatioExceeds7x_FlagsWarning()
    {
        // Low confidence allows ≤3x. 1.0 to 4.0 is 4.0x → should flag
        var workItems = new[]
        {
            CreateWorkItem("WI-001", "SELECT 1", ConfidenceLevel.Low, minHours: 1.0, maxHours: 4.0)
        };

        var result = _validator.Validate(workItems, null);

        result.Passed.Should().BeFalse();
        result.Warnings.Should().Contain(w =>
            w.Category == "effort-range" && w.Message.Contains("3.0x"));
    }

    [Fact]
    public void Validate_NegativeMinHours_FlagsWarning()
    {
        var workItems = new[]
        {
            CreateWorkItem("WI-001", "SELECT 1", ConfidenceLevel.High, minHours: -1.0, maxHours: 2.0)
        };

        var result = _validator.Validate(workItems, null);

        result.Passed.Should().BeFalse();
        result.Warnings.Should().Contain(w =>
            w.Category == "effort-range" && w.Message.Contains("negative"));
    }

    #endregion

    #region Integration: Validation Runs Automatically in Generator

    [Fact]
    public void WorkItemGeneratorService_IncludesValidationSummary()
    {
        // Use a minimal setup to verify the generator includes validation
        var grouper = new StatementGrouper(
            Microsoft.Extensions.Logging.Abstractions.NullLogger<StatementGrouper>.Instance);
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

        var service = new WorkItemGeneratorService(
            grouper, priorityCalculator, effortEstimator, knowledgeBase, conversionEngine,
            deduplicator, titleGenerator, descriptionGenerator, guidanceGenerator,
            acceptanceCriteriaGenerator, jsonReader, jsonWriter, markdownWriter);

        var statements = new[]
        {
            new AnalyzedStatement
            {
                Source = new CollectedStatement
                {
                    SqlText = "SELECT TOP 10 * FROM Orders",
                    Source = StatementSource.QueryStore,
                    QueryHash = "abc123",
                    ExecutionCount = 50
                },
                Classification = StatementClassification.Select,
                Features = new[]
                {
                    new DetectedFeature
                    {
                        FeatureName = "TOP",
                        Category = FeatureCategory.QueryFeature,
                        StatementId = "test",
                        Line = 1,
                        Column = 1
                    }
                },
                RiskScore = 2,
                WeightedRisk = 100,
                ParseSucceeded = true
            }
        };

        var config = new WorkItemConfiguration { MinimumRiskLevel = 1 };
        var featureDetection = new FeatureDetectionResult
        {
            FeatureCounts = new Dictionary<string, int>(),
            DetailedInventory = Array.Empty<DetectedServerFeature>(),
            InaccessibleFeatures = Array.Empty<InaccessibleFeature>()
        };

        var result = service.GenerateWorkItems(statements, featureDetection, config);

        result.Succeeded.Should().BeTrue();
        result.ValidationSummary.Should().NotBeNull(
            "the generator should always run validation and include the summary");
        result.ValidationSummary!.Passed.Should().BeTrue(
            "valid work items should produce zero warnings");
        result.ValidationSummary.WarningCount.Should().Be(0);
    }

    #endregion

    #region Helpers

    private static WorkItem CreateWorkItem(
        string id,
        string postgresEquivalent,
        ConfidenceLevel confidence,
        double minHours = 1.0,
        double maxHours = 2.0)
    {
        return new WorkItem
        {
            Id = id,
            Title = $"[Risk 2] Convert feature in object",
            Description = "Test description",
            SqlServerPattern = "SELECT * FROM T",
            PostgresEquivalent = postgresEquivalent,
            AffectedObjects = new[] { new AffectedObject { Name = "Ad Hoc Queries", Type = "AdHoc", StatementCount = 1 } },
            RiskLevel = 2,
            Priority = "Medium",
            PriorityScore = 100,
            EstimatedEffort = new HourRange { MinHours = minHours, MaxHours = maxHours },
            ConfidenceLevel = confidence,
            AcceptanceCriteria = new[] { "Test passes" },
            RemediationGuidance = "Replace with PostgreSQL equivalent",
            Tags = new[] { "risk-2" },
            DetectedFeatures = new[] { "TOP" }
        };
    }

    private static WorkItem CreateWorkItemWithObject(
        string id,
        string objectName,
        string objectType,
        IReadOnlyList<string> features)
    {
        return new WorkItem
        {
            Id = id,
            Title = $"[Risk 4] Convert feature in {objectName}",
            Description = "Test description",
            SqlServerPattern = "SELECT * FROM T WITH (NOLOCK)",
            PostgresEquivalent = "-- TODO: verify\nSELECT * FROM T",
            AffectedObjects = new[] { new AffectedObject { Name = objectName, Type = objectType, StatementCount = 1 } },
            RiskLevel = 4,
            Priority = "High",
            PriorityScore = 200,
            EstimatedEffort = new HourRange { MinHours = 4.0, MaxHours = 12.0 },
            ConfidenceLevel = ConfidenceLevel.Low,
            AcceptanceCriteria = new[] { "Test passes" },
            RemediationGuidance = "Review locking strategy",
            Tags = new[] { "risk-4" },
            DetectedFeatures = features
        };
    }

    #endregion
}
