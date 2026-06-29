using FluentAssertions;
using MigrationAssessment.Core.Models;
using MigrationAssessment.WorkItems.Models;
using NSubstitute;

namespace MigrationAssessment.WorkItems.Tests;

/// <summary>
/// Unit tests for content generation components: RemediationKnowledgeBase,
/// TitleGenerator, RemediationGuidanceGenerator, and AcceptanceCriteriaGenerator.
/// Validates: Requirements 3.2, 3.7, 4.1, 4.2, 4.3, 4.4, 4.6, 8.1, 8.2, 8.3, 8.4, 8.5
/// </summary>
public class ContentGenerationUnitTests
{
    private readonly RemediationKnowledgeBase _knowledgeBase = new();
    private readonly TitleGenerator _titleGenerator = new();
    private readonly AcceptanceCriteriaGenerator _acceptanceCriteriaGenerator = new();

    #region 1. KnowledgeBase_ReturnsEntries_ForAllKnownFeatures

    /// <summary>
    /// Verifies that the knowledge base returns a non-null entry with correct risk level
    /// for all known SQL Server features.
    /// </summary>
    [Theory]
    [InlineData("TOP", 2)]
    [InlineData("ISNULL", 2)]
    [InlineData("GETDATE", 2)]
    [InlineData("LEN", 2)]
    [InlineData("CHARINDEX", 2)]
    [InlineData("PATINDEX", 2)]
    [InlineData("STUFF", 2)]
    [InlineData("DATEADD", 2)]
    [InlineData("DATEDIFF", 2)]
    [InlineData("DATEPART", 2)]
    [InlineData("TRY_CATCH", 3)]
    [InlineData("DYNAMIC_SQL", 3)]
    [InlineData("TEMP_TABLE", 3)]
    [InlineData("OUTPUT", 3)]
    [InlineData("CROSS_APPLY", 3)]
    [InlineData("OUTER_APPLY", 3)]
    [InlineData("MERGE", 4)]
    [InlineData("TABLE_VALUED_PARAMETER", 4)]
    [InlineData("GLOBAL_TEMP_TABLE", 4)]
    [InlineData("NOLOCK", 4)]
    [InlineData("ROWLOCK", 4)]
    [InlineData("UPDLOCK", 4)]
    [InlineData("PIVOT", 4)]
    [InlineData("UNPIVOT", 4)]
    [InlineData("SQL_CLR", 5)]
    [InlineData("SERVICE_BROKER", 5)]
    [InlineData("LINKED_SERVER", 5)]
    [InlineData("XML_METHOD", 5)]
    [InlineData("OPENQUERY", 5)]
    [InlineData("OPENROWSET", 5)]
    [InlineData("FILESTREAM", 5)]
    [InlineData("MEMORY_OPTIMIZED", 5)]
    public void KnowledgeBase_ReturnsEntries_ForAllKnownFeatures(string featureName, int expectedRiskLevel)
    {
        var entry = _knowledgeBase.GetGuidance(featureName);

        entry.Should().NotBeNull($"knowledge base should have an entry for '{featureName}'");
        entry!.RiskLevel.Should().Be(expectedRiskLevel,
            $"'{featureName}' should have risk level {expectedRiskLevel}");
        entry.PostgresEquivalent.Should().NotBeNullOrWhiteSpace();
        entry.RemediationSteps.Should().NotBeNullOrWhiteSpace();
        entry.IncompatibilityExplanation.Should().NotBeNullOrWhiteSpace();
        _knowledgeBase.HasGuidance(featureName).Should().BeTrue();
    }

    #endregion

    #region 2. KnowledgeBase_ReturnsNull_ForUnknownFeature

    /// <summary>
    /// Verifies that GetGuidance returns null and HasGuidance returns false
    /// for a feature not in the knowledge base.
    /// </summary>
    [Fact]
    public void KnowledgeBase_ReturnsNull_ForUnknownFeature()
    {
        var entry = _knowledgeBase.GetGuidance("UNKNOWN_FEATURE");

        entry.Should().BeNull("unknown features should return null from GetGuidance");
        _knowledgeBase.HasGuidance("UNKNOWN_FEATURE").Should().BeFalse(
            "HasGuidance should return false for unknown features");
    }

    #endregion

    #region 3. TitleGenerator_TruncatesObjectName_WhenExceedingMaxLength

    /// <summary>
    /// Verifies that a very long object name (200+ chars) results in a title
    /// that is ≤120 characters and ends with "...".
    /// </summary>
    [Fact]
    public void TitleGenerator_TruncatesObjectName_WhenExceedingMaxLength()
    {
        var longObjectName = new string('A', 200);

        var title = _titleGenerator.GenerateTitle("TOP", longObjectName, 3);

        title.Length.Should().BeLessThanOrEqualTo(120,
            "title must not exceed 120 characters");
        title.Should().EndWith("...",
            "truncated titles should end with '...'");
        title.Should().StartWith("[Risk 3] Convert TOP in ");
    }

    #endregion

    #region 4. TitleGenerator_UsesAdHocQueries_WhenObjectNameIsNull

    /// <summary>
    /// Verifies that a null object name produces a title containing "Ad Hoc Queries".
    /// </summary>
    [Fact]
    public void TitleGenerator_UsesAdHocQueries_WhenObjectNameIsNull()
    {
        var title = _titleGenerator.GenerateTitle("ISNULL", null, 2);

        title.Should().Contain("Ad Hoc Queries",
            "null object name should be replaced with 'Ad Hoc Queries'");
        title.Should().Be("[Risk 2] Convert ISNULL in Ad Hoc Queries");
    }

    #endregion

    #region 5. TitleGenerator_ProducesCorrectFormat

    /// <summary>
    /// Verifies the title format is "[Risk N] Convert X in Y" for known inputs.
    /// </summary>
    [Fact]
    public void TitleGenerator_ProducesCorrectFormat()
    {
        var title = _titleGenerator.GenerateTitle("MERGE", "dbo.UpsertCustomers", 4);

        title.Should().Be("[Risk 4] Convert MERGE in dbo.UpsertCustomers",
            "title should follow the format '[Risk N] Convert feature in object'");
    }

    #endregion

    #region 6. RemediationGuidance_SetsRequiresResearch_ForUnknownFeature

    /// <summary>
    /// Verifies that GenerateGuidance with an unknown feature returns RequiresResearch = true.
    /// </summary>
    [Fact]
    public void RemediationGuidance_SetsRequiresResearch_ForUnknownFeature()
    {
        var generator = new RemediationGuidanceGenerator(_knowledgeBase);

        var (guidance, requiresResearch) = generator.GenerateGuidance(
            "COMPLETELY_UNKNOWN_FEATURE", "SELECT * FROM SomeTable");

        requiresResearch.Should().BeTrue(
            "unknown features should set the requires-research flag");
        guidance.Should().Contain("Manual analysis required",
            "unknown features should indicate manual analysis is needed");
    }

    #endregion

    #region 7. RemediationGuidance_IncludesBeforeAndAfter_ForKnownFeature

    /// <summary>
    /// Verifies that guidance for a known feature contains "Before (SQL Server)"
    /// and "PostgreSQL equivalent" sections.
    /// </summary>
    [Fact]
    public void RemediationGuidance_IncludesBeforeAndAfter_ForKnownFeature()
    {
        var generator = new RemediationGuidanceGenerator(_knowledgeBase);

        var (guidance, requiresResearch) = generator.GenerateGuidance(
            "TOP", "SELECT TOP 10 * FROM Orders");

        requiresResearch.Should().BeFalse(
            "known features should not require research");
        guidance.Should().Contain("Before (SQL Server)",
            "guidance should include a 'Before (SQL Server)' section");
        guidance.Should().Contain("PostgreSQL equivalent",
            "guidance should include a PostgreSQL equivalent section");
        guidance.Should().Contain("SELECT TOP 10 * FROM Orders",
            "guidance should include the actual SQL text");
    }

    #endregion

    #region 8. AcceptanceCriteria_ContainsAtLeastTwoItems_ForAnyRiskLevel

    /// <summary>
    /// Verifies that acceptance criteria always contains at least 2 items
    /// regardless of risk level.
    /// </summary>
    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    public void AcceptanceCriteria_ContainsAtLeastTwoItems_ForAnyRiskLevel(int riskLevel)
    {
        var criteria = _acceptanceCriteriaGenerator.GenerateCriteria(
            "TOP", riskLevel, "dbo.TestProc");

        criteria.Should().HaveCountGreaterThanOrEqualTo(2,
            $"risk level {riskLevel} should produce at least 2 acceptance criteria");
        criteria.Should().AllSatisfy(c =>
            c.Should().NotBeNullOrWhiteSpace("each criterion should be non-empty"));
    }

    #endregion

    #region 9. AcceptanceCriteria_ContainsExtraCriteria_ForHighRisk

    /// <summary>
    /// Verifies that Risk 4 gets 4 criteria and Risk 5 gets 5 criteria.
    /// </summary>
    [Fact]
    public void AcceptanceCriteria_ContainsExtraCriteria_ForHighRisk()
    {
        var risk4Criteria = _acceptanceCriteriaGenerator.GenerateCriteria(
            "MERGE", 4, "dbo.UpsertProc");
        var risk5Criteria = _acceptanceCriteriaGenerator.GenerateCriteria(
            "XML_METHOD", 5, "dbo.ProcessXml");

        risk4Criteria.Should().HaveCount(4,
            "Risk 4 should produce exactly 4 acceptance criteria");
        risk5Criteria.Should().HaveCount(5,
            "Risk 5 should produce exactly 5 acceptance criteria");
    }

    #endregion

    #region 10. TitleGenerator_SingleFeatureOverload_DelegatesToMultiFeatureOverload

    /// <summary>
    /// Verifies that the single-feature GenerateTitle(string, string?, int) overload
    /// delegates to the multi-feature overload and produces identical output.
    /// Validates: Requirement 8.2
    /// </summary>
    [Theory]
    [InlineData("ISNULL", "dbo.Orders", 2)]
    [InlineData("MERGE", null, 4)]
    [InlineData("TOP", "dbo.GetCustomers", 3)]
    public void TitleGenerator_SingleFeatureOverload_DelegatesToMultiFeatureOverload(
        string featureName, string? objectName, int riskLevel)
    {
        var singleFeatureResult = _titleGenerator.GenerateTitle(featureName, objectName, riskLevel);
        var multiFeatureResult = _titleGenerator.GenerateTitle(
            new[] { featureName }, objectName, riskLevel);

        singleFeatureResult.Should().Be(multiFeatureResult,
            "single-feature overload should delegate to multi-feature overload and produce the same output");
    }

    #endregion

    #region 11. DescriptionGenerator_SingleFeatureOverload_DelegatesToMultiFeatureOverload

    /// <summary>
    /// Verifies that the single-feature GenerateDescription overload delegates to
    /// the multi-feature overload and produces identical output.
    /// Validates: Requirement 8.3
    /// </summary>
    [Theory]
    [InlineData("ISNULL", 2, 5, 1000L, "dbo.Orders")]
    [InlineData("MERGE", 4, 1, 50000L, null)]
    [InlineData("TOP", 3, 10, 0L, "dbo.GetTopN")]
    public void DescriptionGenerator_SingleFeatureOverload_DelegatesToMultiFeatureOverload(
        string featureName, int riskLevel, int occurrenceCount, long totalExecutionCount, string? objectName)
    {
        var descriptionGenerator = new DescriptionGenerator();

        var singleFeatureResult = descriptionGenerator.GenerateDescription(
            featureName, riskLevel, occurrenceCount, totalExecutionCount, objectName);
        var multiFeatureResult = descriptionGenerator.GenerateDescription(
            new[] { featureName }, riskLevel, occurrenceCount, totalExecutionCount, objectName);

        singleFeatureResult.Should().Be(multiFeatureResult,
            "single-feature overload should delegate to multi-feature overload and produce the same output");
    }

    #endregion

    #region 12. RemediationGuidanceGenerator_SingleFeatureOverload_DelegatesToMultiFeatureOverload

    /// <summary>
    /// Verifies that the single-feature GenerateGuidance(string, string) overload
    /// delegates to the multi-feature overload and produces identical output.
    /// Validates: Requirement 8.4
    /// </summary>
    [Theory]
    [InlineData("TOP", "SELECT TOP 10 * FROM Orders")]
    [InlineData("ISNULL", "SELECT ISNULL(col, 0) FROM dbo.Accounts")]
    [InlineData("MERGE", "MERGE INTO dbo.Target USING dbo.Source ON ...")]
    public void RemediationGuidanceGenerator_SingleFeatureOverload_DelegatesToMultiFeatureOverload(
        string featureName, string primarySqlText)
    {
        var generator = new RemediationGuidanceGenerator(_knowledgeBase);

        var (singleGuidance, singleResearch) = generator.GenerateGuidance(featureName, primarySqlText);
        var (multiGuidance, multiResearch) = generator.GenerateGuidance(
            new[] { featureName }, primarySqlText);

        singleGuidance.Should().Be(multiGuidance,
            "single-feature overload should delegate to multi-feature overload and produce the same guidance");
        singleResearch.Should().Be(multiResearch,
            "single-feature overload should delegate to multi-feature overload and produce the same RequiresResearch flag");
    }

    #endregion

    #region 13. EffortEstimator_SingleRiskOverload_WorksIndependently

    /// <summary>
    /// Verifies that EstimateEffort(int riskLevel, int statementCount) still works
    /// independently and produces valid effort ranges.
    /// Validates: Requirement 8.5
    /// </summary>
    [Theory]
    [InlineData(1, 1)]
    [InlineData(2, 5)]
    [InlineData(3, 3)]
    [InlineData(4, 1)]
    [InlineData(5, 2)]
    public void EffortEstimator_SingleRiskOverload_WorksIndependently(int riskLevel, int statementCount)
    {
        IEffortEstimator estimator = new EffortEstimator();

        var result = estimator.EstimateEffort(riskLevel, statementCount);

        result.MinHours.Should().BeGreaterThanOrEqualTo(0,
            "minimum effort should be non-negative");
        result.MaxHours.Should().BeGreaterThanOrEqualTo(result.MinHours,
            "maximum effort should be >= minimum effort");
    }

    /// <summary>
    /// Verifies that EstimateEffort(int, int) returns zero for zero or negative statement count.
    /// Validates: Requirement 8.5
    /// </summary>
    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void EffortEstimator_SingleRiskOverload_ReturnsZeroForInvalidStatementCount(int statementCount)
    {
        IEffortEstimator estimator = new EffortEstimator();

        var result = estimator.EstimateEffort(3, statementCount);

        result.MinHours.Should().Be(0);
        result.MaxHours.Should().Be(0);
    }

    #endregion

    #region 14. IStatementGrouper_InterfaceSignatures_AreUnchanged

    /// <summary>
    /// Verifies that the IStatementGrouper interface still exposes the expected method signatures.
    /// This is a compile-time + runtime check that the interface contract is preserved.
    /// Validates: Requirement 8.1
    /// </summary>
    [Fact]
    public void IStatementGrouper_InterfaceSignatures_AreUnchanged()
    {
        var interfaceType = typeof(IStatementGrouper);

        // Verify the two-overload GroupStatements methods exist with correct parameter types
        var methods = interfaceType.GetMethods()
            .Where(m => m.Name == "GroupStatements")
            .ToList();

        methods.Should().HaveCount(2,
            "IStatementGrouper should have exactly 2 GroupStatements overloads");

        // Verify 3-parameter overload: (IReadOnlyList<AnalyzedStatement>, FeatureDetectionResult, int)
        var threeParamOverload = methods.FirstOrDefault(m => m.GetParameters().Length == 3);
        threeParamOverload.Should().NotBeNull(
            "IStatementGrouper should have a 3-parameter GroupStatements overload");
        var threeParams = threeParamOverload!.GetParameters();
        threeParams[0].ParameterType.Should().Be(typeof(IReadOnlyList<AnalyzedStatement>));
        threeParams[1].ParameterType.Should().Be(typeof(FeatureDetectionResult));
        threeParams[2].ParameterType.Should().Be(typeof(int));

        // Verify 4-parameter overload: (IReadOnlyList<AnalyzedStatement>, FeatureDetectionResult, int, IReadOnlyList<ObjectInventoryEntry>)
        var fourParamOverload = methods.FirstOrDefault(m => m.GetParameters().Length == 4);
        fourParamOverload.Should().NotBeNull(
            "IStatementGrouper should have a 4-parameter GroupStatements overload");
        var fourParams = fourParamOverload!.GetParameters();
        fourParams[0].ParameterType.Should().Be(typeof(IReadOnlyList<AnalyzedStatement>));
        fourParams[1].ParameterType.Should().Be(typeof(FeatureDetectionResult));
        fourParams[2].ParameterType.Should().Be(typeof(int));
        fourParams[3].ParameterType.Should().Be(typeof(IReadOnlyList<ObjectInventoryEntry>));

        // Verify return types
        threeParamOverload.ReturnType.Should().Be(typeof(IReadOnlyList<StatementGroup>));
        fourParamOverload.ReturnType.Should().Be(typeof(IReadOnlyList<StatementGroup>));
    }

    #endregion
}
