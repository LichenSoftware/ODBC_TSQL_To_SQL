using FluentAssertions;
using FsCheck;
using FsCheck.Xunit;
using Microsoft.Extensions.Logging.Abstractions;
using MigrationAssessment.Core.Models;
using MigrationAssessment.WorkItems.Models;

namespace MigrationAssessment.WorkItems.Tests;

/// <summary>
/// Property-based tests for the StatementGrouper.
/// Validates: Requirements 2.1, 2.2, 2.3, 2.4, 2.5, 2.6, 8.1
/// </summary>
public class StatementGrouperPropertyTests
{
    private static readonly StatementGrouper Grouper =
        new(NullLogger<StatementGrouper>.Instance);

    /// <summary>
    /// Known features with their risk levels from the StatementGrouper.FeatureRiskMap.
    /// </summary>
    private static readonly (string Name, int Risk)[] KnownFeatures =
    [
        ("TOP", 2), ("ISNULL", 2), ("GETDATE", 2), ("LEN", 2),
        ("CHARINDEX", 2), ("PATINDEX", 2), ("STUFF", 2),
        ("DATEADD", 2), ("DATEDIFF", 2), ("DATEPART", 2),
        ("OFFSET_FETCH", 2), ("STRING_CONCAT", 2),
        ("STRING_CONCAT_PLUS", 2), ("TOP_WITHOUT_ORDER", 2),
        ("PRINT_STATEMENT", 2), ("THROW", 2),
        ("IMPLICIT_CONVERSION", 2), ("STRING_SPLIT", 2),
        ("TRY_CATCH", 3), ("DYNAMIC_SQL", 3), ("EXPLICIT_TRANSACTION", 3),
        ("SAVEPOINT", 3), ("TEMP_TABLE", 3), ("OUTPUT", 3),
        ("CROSS_APPLY", 3), ("OUTER_APPLY", 3), ("JSON_METHOD", 3),
        ("IDENTITY", 3), ("CTE", 3), ("RAISERROR", 3),
        ("MERGE", 4), ("TABLE_VALUED_PARAMETER", 4), ("TABLE_VARIABLE", 4),
        ("GLOBAL_TEMP_TABLE", 4), ("NOLOCK", 4), ("ROWLOCK", 4),
        ("UPDLOCK", 4), ("PIVOT", 4), ("UNPIVOT", 4),
        ("OPENJSON", 4), ("FOR_XML", 4),
        ("OPENQUERY", 5), ("OPENROWSET", 5), ("XML_METHOD", 5),
        ("SQL_CLR", 5), ("SERVICE_BROKER", 5), ("LINKED_SERVER", 5),
        ("REPLICATION", 5), ("FILESTREAM", 5), ("MEMORY_OPTIMIZED", 5),
    ];

    private static FeatureDetectionResult EmptyFeatureDetection => new()
    {
        FeatureCounts = new Dictionary<string, int>(),
        DetailedInventory = Array.Empty<DetectedServerFeature>(),
        InaccessibleFeatures = Array.Empty<InaccessibleFeature>()
    };

    #region Generators

    private static Gen<CollectedStatement> GenCollectedStatement()
    {
        return from sqlText in Arb.Generate<NonEmptyString>()
               from source in Gen.Elements(
                   StatementSource.QueryStore,
                   StatementSource.ExtendedEvents,
                   StatementSource.Metadata)
               from hash in Arb.Generate<NonEmptyString>()
               select new CollectedStatement
               {
                   SqlText = sqlText.Get,
                   Source = source,
                   QueryHash = hash.Get,
                   ExecutionCount = 1
               };
    }

    private static Gen<DetectedFeature> GenDetectedFeature(string featureName)
    {
        return from category in Gen.Elements(
                   FeatureCategory.QueryFeature,
                   FeatureCategory.FunctionUsage,
                   FeatureCategory.TemporaryObject,
                   FeatureCategory.TransactionFeature)
               from stmtId in Arb.Generate<NonEmptyString>()
               select new DetectedFeature
               {
                   FeatureName = featureName,
                   Category = category,
                   StatementId = stmtId.Get,
                   Line = 1,
                   Column = 1
               };
    }

    private static Gen<AnalyzedStatement> GenAnalyzedStatementWithFeatures(
        IReadOnlyList<string> featureNames)
    {
        return from source in GenCollectedStatement()
               from features in Gen.Sequence(
                   featureNames.Select(GenDetectedFeature))
               from riskScore in Gen.Choose(1, 5)
               select new AnalyzedStatement
               {
                   Source = source,
                   Classification = StatementClassification.Select,
                   Features = features.ToList(),
                   RiskScore = riskScore,
                   WeightedRisk = riskScore * 1.0,
                   ParseSucceeded = true
               };
    }

    private static Gen<AnalyzedStatement> GenRandomAnalyzedStatement()
    {
        return from featureCount in Gen.Choose(1, 3)
               from featureIndices in Gen.ListOf(featureCount,
                   Gen.Choose(0, KnownFeatures.Length - 1))
               let featureNames = featureIndices.Select(i => KnownFeatures[i].Name).Distinct().ToList()
               from stmt in GenAnalyzedStatementWithFeatures(featureNames)
               select stmt;
    }

    /// <summary>
    /// Generates an AnalyzedStatement drawing features only from the provided pool.
    /// Used when we need to ensure statements don't accidentally contain certain feature names.
    /// </summary>
    private static Gen<AnalyzedStatement> GenAnalyzedStatementFromPool(
        (string Name, int Risk)[] featurePool)
    {
        return from featureCount in Gen.Choose(1, Math.Min(3, featurePool.Length))
               from featureIndices in Gen.ListOf(featureCount,
                   Gen.Choose(0, featurePool.Length - 1))
               let featureNames = featureIndices.Select(i => featurePool[i].Name).Distinct().ToList()
               from stmt in GenAnalyzedStatementWithFeatures(featureNames)
               select stmt;
    }

    #endregion

    /// <summary>
    /// Property 1: Grouping key uniqueness — verify that statements with the same
    /// SQL text end up in the same group (grouped by hash).
    /// With the new statement-based grouping, each unique SQL text + object
    /// produces exactly one group.
    /// 
    /// **Validates: Requirements 1.1, 1.2**
    /// </summary>
    [Property(MaxTest = 100)]
    public Property GroupingKeyUniqueness()
    {
        var gen = from count in Gen.Choose(1, 10)
                  from statements in Gen.ListOf(count, GenRandomAnalyzedStatement())
                  select statements.ToList();

        return Prop.ForAll(gen.ToArbitrary(), statements =>
        {
            var groups = Grouper.GroupStatements(
                statements,
                EmptyFeatureDetection,
                minimumRiskLevel: 1);

            // Extract non-server-level groups (statement-based groups)
            var statementGroups = groups
                .Where(g => !g.IsServerLevelFeature)
                .ToList();

            // With statement-based grouping, the number of groups should be at most
            // the number of distinct (SqlText, DatabaseObjectName) pairs among statements
            // that pass the minimum risk filter.
            var distinctSqlTexts = statements
                .Where(s => s.RiskScore >= 1)
                .Where(s => s.Features.Count > 0)
                .Select(s => s.Source.SqlText)
                .Distinct()
                .Count();

            statementGroups.Count.Should().BeLessThanOrEqualTo(distinctSqlTexts,
                "number of groups should be at most the number of distinct SQL texts");
        });
    }

    /// <summary>
    /// Property 2: Multi-feature highest-risk assignment — verify that a statement
    /// with features at different risk levels produces a single group where
    /// FeatureName is set to the highest-risk feature.
    /// 
    /// **Validates: Requirements 1.4**
    /// </summary>
    [Property(MaxTest = 100)]
    public Property MultiFeatureHighestRiskAssignment()
    {
        // Generate a statement with exactly 2 features at DIFFERENT risk levels
        var gen = from lowIdx in Gen.Choose(0, 17)  // Risk 2 features (indices 0-17)
                  from highIdx in Gen.Choose(18, KnownFeatures.Length - 1)  // Risk 3+ features
                  let lowFeature = KnownFeatures[lowIdx]
                  let highFeature = KnownFeatures[highIdx]
                  where lowFeature.Risk != highFeature.Risk
                  from stmt in GenAnalyzedStatementWithFeatures(
                      new[] { lowFeature.Name, highFeature.Name })
                  // Ensure risk score is high enough to pass the minimum filter
                  let adjustedStmt = stmt with { RiskScore = highFeature.Risk }
                  select (adjustedStmt, lowFeature, highFeature);

        return Prop.ForAll(gen.ToArbitrary(), tuple =>
        {
            var (statement, lowFeature, highFeature) = tuple;

            var groups = Grouper.GroupStatements(
                new[] { statement },
                EmptyFeatureDetection,
                minimumRiskLevel: 1);

            var statementGroups = groups
                .Where(g => !g.IsServerLevelFeature)
                .ToList();

            // With statement-based grouping, a single statement produces exactly one group
            statementGroups.Count.Should().Be(1,
                "a single statement should produce exactly one group");

            var group = statementGroups[0];

            // The FeatureName should be the highest-risk feature
            group.FeatureName.Should().Be(highFeature.Name,
                $"FeatureName should be the highest-risk feature (risk {highFeature.Risk}), " +
                $"not '{lowFeature.Name}' (risk {lowFeature.Risk})");

            // DetectedFeatures should contain BOTH features
            group.DetectedFeatures.Should().Contain(
                f => f.Equals(highFeature.Name, StringComparison.OrdinalIgnoreCase),
                "DetectedFeatures should include the high-risk feature");
            group.DetectedFeatures.Should().Contain(
                f => f.Equals(lowFeature.Name, StringComparison.OrdinalIgnoreCase),
                "DetectedFeatures should include the low-risk feature");
        });
    }

    /// <summary>
    /// Property 3: Same-risk multi-feature inclusion — verify that a statement with
    /// multiple same-risk features produces a single group with all features
    /// in DetectedFeatures.
    /// 
    /// **Validates: Requirements 1.3**
    /// </summary>
    [Property(MaxTest = 100)]
    public Property SameRiskMultiFeatureInclusion()
    {
        // Generate a statement with 2+ features at the SAME risk level
        var gen = from riskLevel in Gen.Elements(2, 3, 4, 5)
                  let featuresAtRisk = KnownFeatures
                      .Where(f => f.Risk == riskLevel)
                      .Select(f => f.Name)
                      .ToArray()
                  where featuresAtRisk.Length >= 2
                  from featureCount in Gen.Choose(2, Math.Min(3, featuresAtRisk.Length))
                  from selectedIndices in Gen.ListOf(featureCount,
                      Gen.Choose(0, featuresAtRisk.Length - 1))
                  let distinctFeatures = selectedIndices.Select(i => featuresAtRisk[i]).Distinct().ToList()
                  where distinctFeatures.Count >= 2
                  from stmt in GenAnalyzedStatementWithFeatures(distinctFeatures)
                  let adjustedStmt = stmt with { RiskScore = riskLevel }
                  select (adjustedStmt, distinctFeatures, riskLevel);

        return Prop.ForAll(gen.ToArbitrary(), tuple =>
        {
            var (statement, expectedFeatures, riskLevel) = tuple;

            var groups = Grouper.GroupStatements(
                new[] { statement },
                EmptyFeatureDetection,
                minimumRiskLevel: 1);

            var statementGroups = groups
                .Where(g => !g.IsServerLevelFeature)
                .ToList();

            // With statement-based grouping, a single statement produces exactly one group
            statementGroups.Count.Should().Be(1,
                "a single statement should produce exactly one group under statement-based grouping");

            var group = statementGroups[0];

            // The group should contain ALL features from the statement in DetectedFeatures
            foreach (var expectedFeature in expectedFeatures)
            {
                group.DetectedFeatures.Should().Contain(
                    f => f.Equals(expectedFeature, StringComparison.OrdinalIgnoreCase),
                    $"DetectedFeatures should include '{expectedFeature}'");
            }

            // DetectedFeatures count should match the distinct features on the statement
            group.DetectedFeatures.Count.Should().Be(expectedFeatures.Count,
                "DetectedFeatures should contain all distinct features from the statement");
        });
    }

    /// <summary>
    /// Property 1 (Design): Statement-based grouping reduces work item count.
    /// For any set of analyzed statements where at least one statement contains multiple
    /// detected features in the same database object, the number of StatementGroup records
    /// produced by statement-based grouping SHALL be less than or equal to the number
    /// produced by the old feature-based grouping.
    /// 
    /// **Validates: Requirements 1**
    /// </summary>
    [Property(MaxTest = 200)]
    public Property StatementBasedGroupingReducesWorkItemCount()
    {
        // Generate multiple statements, each with 2-3 features, to ensure
        // at least some statements have multiple features
        var gen = from count in Gen.Choose(1, 8)
                  from statements in Gen.ListOf(count, GenRandomAnalyzedStatement())
                  // Ensure at least one statement has multiple features
                  where statements.Any(s => s.Features.Select(f => f.FeatureName)
                      .Distinct(StringComparer.OrdinalIgnoreCase).Count() > 1)
                  select statements.ToList();

        return Prop.ForAll(gen.ToArbitrary(), statements =>
        {
            // Run statement-based grouping (the actual implementation)
            var actualGroups = Grouper.GroupStatements(
                statements,
                EmptyFeatureDetection,
                minimumRiskLevel: 1);

            var statementGroups = actualGroups
                .Where(g => !g.IsServerLevelFeature)
                .ToList();

            // Calculate what the OLD feature-based grouping would produce:
            // Count = number of distinct (FeatureName, DatabaseObjectName) pairs
            // Since no object inventory is provided, DatabaseObjectName is always null.
            // So feature-based count = number of distinct feature names across all statements.
            var featureBasedCount = statements
                .Where(s => s.RiskScore >= 1)
                .SelectMany(s => s.Features)
                .Select(f => f.FeatureName)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Count();

            // Statement-based grouping should produce ≤ groups than feature-based grouping
            statementGroups.Count.Should().BeLessThanOrEqualTo(featureBasedCount,
                "statement-based grouping should produce fewer or equal groups compared to " +
                "feature-based grouping when statements contain multiple features");
        });
    }

    /// <summary>
    /// Property 6: MaxRiskLevel equals highest feature risk — verify that for any
    /// StatementGroup produced, MaxRiskLevel equals the maximum of
    /// GetFeatureRiskLevel(f) for all f in DetectedFeatures.
    /// 
    /// **Validates: Requirements 1.5**
    /// </summary>
    [Property(MaxTest = 100)]
    public Property MaxRiskLevelEqualsHighestFeatureRisk()
    {
        var gen = from count in Gen.Choose(1, 10)
                  from statements in Gen.ListOf(count, GenRandomAnalyzedStatement())
                  select statements.ToList();

        return Prop.ForAll(gen.ToArbitrary(), statements =>
        {
            var groups = Grouper.GroupStatements(
                statements,
                EmptyFeatureDetection,
                minimumRiskLevel: 1);

            var statementGroups = groups
                .Where(g => !g.IsServerLevelFeature)
                .ToList();

            foreach (var group in statementGroups)
            {
                // Compute the expected max risk level from DetectedFeatures
                var expectedMaxRisk = group.DetectedFeatures
                    .Select(f => StatementGrouper.GetFeatureRiskLevel(f))
                    .Max();

                group.MaxRiskLevel.Should().Be(expectedMaxRisk,
                    $"MaxRiskLevel should equal the highest risk among DetectedFeatures " +
                    $"[{string.Join(", ", group.DetectedFeatures)}]");
            }
        });
    }

    /// <summary>
    /// Property 4: Server-level feature coverage — verify exactly N server-level
    /// work items for N features with count > 0.
    /// 
    /// **Validates: Requirements 2.5**
    /// </summary>
    [Property(MaxTest = 100)]
    public Property ServerLevelFeatureCoverage()
    {
        // Generate a FeatureDetectionResult with some features having count > 0
        var featureNames = new[] { "SQL_CLR", "SERVICE_BROKER", "LINKED_SERVER",
            "REPLICATION", "FILESTREAM", "MEMORY_OPTIMIZED" };

        var gen = from counts in Gen.ListOf(featureNames.Length,
                      Gen.Choose(0, 5))
                  let featureCounts = featureNames
                      .Zip(counts, (name, count) => (name, count))
                      .ToDictionary(x => x.name, x => x.count)
                  select featureCounts;

        return Prop.ForAll(gen.ToArbitrary(), featureCounts =>
        {
            var featureDetection = new FeatureDetectionResult
            {
                FeatureCounts = featureCounts,
                DetailedInventory = Array.Empty<DetectedServerFeature>(),
                InaccessibleFeatures = Array.Empty<InaccessibleFeature>()
            };

            var groups = Grouper.GroupStatements(
                Array.Empty<AnalyzedStatement>(),
                featureDetection,
                minimumRiskLevel: 1);

            var serverLevelGroups = groups
                .Where(g => g.IsServerLevelFeature)
                .ToList();

            var expectedCount = featureCounts.Count(kvp => kvp.Value > 0);

            // Exactly N server-level work items for N features with count > 0
            serverLevelGroups.Count.Should().Be(expectedCount,
                "there should be exactly one server-level group per feature with count > 0");

            // Each feature with count > 0 should have exactly one server-level group
            var featuresWithPositiveCount = featureCounts
                .Where(kvp => kvp.Value > 0)
                .Select(kvp => kvp.Key)
                .ToList();

            foreach (var featureName in featuresWithPositiveCount)
            {
                serverLevelGroups.Should().ContainSingle(
                    g => g.FeatureName.Equals(featureName, StringComparison.OrdinalIgnoreCase),
                    $"feature '{featureName}' with count > 0 should have exactly one server-level group");
            }

            // Features with count = 0 should NOT have a server-level group
            var featuresWithZeroCount = featureCounts
                .Where(kvp => kvp.Value == 0)
                .Select(kvp => kvp.Key)
                .ToList();

            foreach (var featureName in featuresWithZeroCount)
            {
                serverLevelGroups.Should().NotContain(
                    g => g.FeatureName.Equals(featureName, StringComparison.OrdinalIgnoreCase),
                    $"feature '{featureName}' with count = 0 should NOT have a server-level group");
            }
        });
    }

    /// <summary>
    /// Property 7: Server-level features remain isolated from statement-based groups.
    /// For any input containing server-level features in FeatureDetectionResult, each
    /// server-level feature SHALL produce its own separate StatementGroup with
    /// IsServerLevelFeature = true, unaffected by statement-based grouping.
    /// 
    /// **Validates: Requirements 2**
    /// </summary>
    [Property(MaxTest = 100)]
    public Property ServerLevelFeaturesRemainIsolatedFromStatementGroups()
    {
        // Server-level feature names from the FeatureRiskMap
        var serverLevelFeatureNames = new[] { "SQL_CLR", "SERVICE_BROKER", "LINKED_SERVER",
            "REPLICATION", "FILESTREAM", "MEMORY_OPTIMIZED" };

        // Non-server-level features only (risk 2-4 features) for generating regular statements
        var nonServerFeatures = KnownFeatures
            .Where(f => !serverLevelFeatureNames.Contains(f.Name, StringComparer.OrdinalIgnoreCase))
            .ToArray();

        var gen = from serverFeatureCount in Gen.Choose(1, serverLevelFeatureNames.Length)
                  from selectedServerIndices in Gen.ListOf(serverFeatureCount,
                      Gen.Choose(0, serverLevelFeatureNames.Length - 1))
                  let activeServerFeatures = selectedServerIndices
                      .Select(i => serverLevelFeatureNames[i])
                      .Distinct()
                      .ToList()
                  // Generate regular statements with NON-server-level features only
                  from regularStatementCount in Gen.Choose(1, 5)
                  from regularStatements in Gen.ListOf(regularStatementCount,
                      GenAnalyzedStatementFromPool(nonServerFeatures))
                  select (activeServerFeatures, regularStatements.ToList());

        return Prop.ForAll(gen.ToArbitrary(), tuple =>
        {
            var (activeServerFeatures, regularStatements) = tuple;

            // Build FeatureDetectionResult with active server-level features
            var featureCounts = activeServerFeatures
                .ToDictionary(f => f, _ => 1);

            var featureDetection = new FeatureDetectionResult
            {
                FeatureCounts = featureCounts,
                DetailedInventory = Array.Empty<DetectedServerFeature>(),
                InaccessibleFeatures = Array.Empty<InaccessibleFeature>()
            };

            var groups = Grouper.GroupStatements(
                regularStatements,
                featureDetection,
                minimumRiskLevel: 1);

            var serverLevelGroups = groups
                .Where(g => g.IsServerLevelFeature)
                .ToList();

            var statementBasedGroups = groups
                .Where(g => !g.IsServerLevelFeature)
                .ToList();

            // 1. Each active server-level feature produces its own separate group
            serverLevelGroups.Count.Should().Be(activeServerFeatures.Count,
                "each active server-level feature should produce its own isolated group");

            // 2. Each server-level group has IsServerLevelFeature = true, exactly one
            //    feature in DetectedFeatures, and that feature matches FeatureName
            foreach (var serverGroup in serverLevelGroups)
            {
                serverGroup.IsServerLevelFeature.Should().BeTrue(
                    "server-level groups must have IsServerLevelFeature = true");

                serverGroup.DetectedFeatures.Should().HaveCount(1,
                    $"server-level group '{serverGroup.FeatureName}' should have exactly one detected feature");

                serverGroup.DetectedFeatures[0].Should().Be(serverGroup.FeatureName,
                    "the single detected feature should match the group's FeatureName");
            }

            // 3. Server-level groups are NOT affected by statement-based grouping:
            //    statement-based groups are separate and not marked as server-level
            foreach (var stmtGroup in statementBasedGroups)
            {
                stmtGroup.IsServerLevelFeature.Should().BeFalse(
                    "statement-based groups should NOT be marked as server-level");
            }

            // 4. Server-level feature names each appear exactly once across
            //    all server-level groups (no duplicates, no missing)
            var serverGroupFeatureNames = serverLevelGroups
                .Select(g => g.FeatureName)
                .ToList();

            foreach (var expectedFeature in activeServerFeatures)
            {
                serverGroupFeatureNames.Should().ContainSingle(
                    f => f.Equals(expectedFeature, StringComparison.OrdinalIgnoreCase),
                    $"server-level feature '{expectedFeature}' should appear in exactly one server-level group");
            }

            // 5. Server-level groups are unaffected by having regular statements present:
            //    The total group count = statement-based groups + server-level groups
            groups.Count.Should().Be(statementBasedGroups.Count + serverLevelGroups.Count,
                "all groups should be either statement-based or server-level");
        });
    }
}
