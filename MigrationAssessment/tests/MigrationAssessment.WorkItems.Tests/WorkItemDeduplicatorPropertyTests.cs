using System.Text.RegularExpressions;
using FluentAssertions;
using FsCheck;
using FsCheck.Xunit;
using Microsoft.Extensions.Logging.Abstractions;
using MigrationAssessment.Core.Models;
using MigrationAssessment.WorkItems.Models;

namespace MigrationAssessment.WorkItems.Tests;

/// <summary>
/// Property-based tests for the WorkItemDeduplicator.
/// Validates: Requirements 8.2, 8.4, 8.5
/// </summary>
public class WorkItemDeduplicatorPropertyTests
{
    private static readonly Regex WorkItemIdPattern = new(@"^WI-\d{3,}$", RegexOptions.Compiled);

    private readonly WorkItemDeduplicator _deduplicator = new();

    #region Generators

    /// <summary>
    /// Creates a valid AnalyzedStatement with the specified WeightedRisk and SqlText.
    /// </summary>
    private static AnalyzedStatement CreateStatement(double weightedRisk, string sqlText, int riskScore = 3)
    {
        return new AnalyzedStatement
        {
            Source = new CollectedStatement
            {
                SqlText = sqlText,
                Source = StatementSource.QueryStore,
                QueryHash = Guid.NewGuid().ToString("N")
            },
            Classification = StatementClassification.Select,
            Features = new[]
            {
                new DetectedFeature
                {
                    FeatureName = "TOP",
                    Category = FeatureCategory.QueryFeature,
                    StatementId = Guid.NewGuid().ToString(),
                    Line = 1,
                    Column = 1
                }
            },
            RiskScore = riskScore,
            WeightedRisk = weightedRisk
        };
    }

    /// <summary>
    /// Creates a StatementGroup with the given statements and optional database object name.
    /// </summary>
    private static StatementGroup CreateGroup(
        IReadOnlyList<AnalyzedStatement> statements,
        string featureName = "TOP",
        string? databaseObjectName = null,
        string databaseObjectType = "StoredProcedure")
    {
        var maxRisk = statements.Max(s => s.RiskScore);
        return new StatementGroup
        {
            FeatureName = featureName,
            DetectedFeatures = new[] { featureName },
            DatabaseObjectName = databaseObjectName,
            DatabaseObjectType = databaseObjectType,
            Statements = statements,
            MaxRiskLevel = maxRisk
        };
    }

    /// <summary>
    /// FsCheck Arbitrary for generating a list of statement groups with valid data.
    /// </summary>
    private static Arbitrary<IReadOnlyList<StatementGroup>> ArbitraryStatementGroups()
    {
        var genGroup = from featureIndex in Gen.Choose(0, 4)
                       from objectIndex in Gen.Choose(0, 9)
                       from stmtCount in Gen.Choose(1, 5)
                       from weightedRisks in Gen.ArrayOf(stmtCount, Arb.Generate<PositiveInt>().Select(p => (double)p.Get))
                       let featureName = new[] { "TOP", "ISNULL", "MERGE", "TRY_CATCH", "XML_METHOD" }[featureIndex]
                       let objectName = $"dbo.Proc{objectIndex}"
                       let statements = weightedRisks.Select((wr, i) =>
                           CreateStatement(wr, $"SELECT {featureName} stmt_{i} FROM table_{objectIndex}")).ToList()
                       select CreateGroup(statements, featureName, objectName);

        var genGroups = from count in Gen.Choose(1, 15)
                        from groups in Gen.ArrayOf(count, genGroup)
                        select (IReadOnlyList<StatementGroup>)groups.ToList();

        return Arb.From(genGroups);
    }

    /// <summary>
    /// FsCheck Arbitrary for generating a group with multiple statements having distinct WeightedRisk values.
    /// </summary>
    private static Arbitrary<StatementGroup> ArbitraryGroupWithDistinctWeights()
    {
        var gen = from stmtCount in Gen.Choose(2, 8)
                  from baseWeight in Arb.Generate<PositiveInt>().Select(p => (double)p.Get)
                  let statements = Enumerable.Range(0, stmtCount)
                      .Select(i => CreateStatement(
                          baseWeight + i * 10.0, // distinct weights, increasing
                          $"SELECT TOP ({i + 1}) * FROM dbo.Orders WHERE Status = {i}"))
                      .ToList()
                  select CreateGroup(statements, "TOP", "dbo.Orders");

        return Arb.From(gen);
    }

    /// <summary>
    /// FsCheck Arbitrary for generating groups where some share the same DatabaseObjectName.
    /// </summary>
    private static Arbitrary<IReadOnlyList<StatementGroup>> ArbitraryGroupsWithSharedObjects()
    {
        var gen = from sharedObjectCount in Gen.Choose(1, 3)
                  from sharedFeatureCount in Gen.Choose(2, 4) // K > 1 features per shared object
                  from extraGroupCount in Gen.Choose(0, 3)
                  let sharedGroups = Enumerable.Range(0, sharedObjectCount)
                      .SelectMany(objIdx =>
                      {
                          var objectName = $"dbo.SharedProc{objIdx}";
                          var features = new[] { "TOP", "ISNULL", "MERGE", "TRY_CATCH", "XML_METHOD" };
                          return Enumerable.Range(0, sharedFeatureCount)
                              .Select(featIdx =>
                              {
                                  var stmt = CreateStatement(
                                      (featIdx + 1) * 10.0,
                                      $"SELECT {features[featIdx % features.Length]} FROM {objectName}");
                                  return CreateGroup(
                                      new[] { stmt },
                                      features[featIdx % features.Length],
                                      objectName);
                              });
                      }).ToList()
                  let extraGroups = Enumerable.Range(0, extraGroupCount)
                      .Select(i =>
                      {
                          var stmt = CreateStatement(5.0, $"SELECT * FROM dbo.UniqueProc{i}");
                          return CreateGroup(new[] { stmt }, "TOP", $"dbo.UniqueProc{i}");
                      }).ToList()
                  let allGroups = sharedGroups.Concat(extraGroups).ToList()
                  select (IReadOnlyList<StatementGroup>)allGroups;

        return Arb.From(gen);
    }

    #endregion

    #region Property 8: Primary example is highest weighted risk

    /// <summary>
    /// **Validates: Requirements 8.2**
    /// Property 8: For any work item containing multiple merged statements, the SQL Server pattern
    /// example SHALL be sourced from the statement with the highest WeightedRisk value.
    /// </summary>
    [Property(Arbitrary = new[] { typeof(WorkItemDeduplicatorPropertyTests) })]
    public Property PrimarySqlPattern_IsFromHighestWeightedRiskStatement(StatementGroup group)
    {
        var result = _deduplicator.Deduplicate(new[] { group });

        if (result.Count == 0) return true.ToProperty();

        var deduplicated = result[0];
        var highestWeightedRisk = group.Statements.Max(s => s.WeightedRisk);
        var highestStatement = group.Statements.First(s => s.WeightedRisk == highestWeightedRisk);
        var expectedPattern = highestStatement.Source.SqlText.Length <= 500
            ? highestStatement.Source.SqlText
            : highestStatement.Source.SqlText[..500];

        return (deduplicated.PrimarySqlPattern == expectedPattern).ToProperty();
    }

    /// <summary>
    /// **Validates: Requirements 8.2**
    /// Property 8 (additional): Verify the PrimaryStatement field is the statement with max WeightedRisk.
    /// </summary>
    [Property(MaxTest = 200, Arbitrary = new[] { typeof(WorkItemDeduplicatorPropertyTests) })]
    public Property PrimaryStatement_HasHighestWeightedRisk(StatementGroup group)
    {
        var result = _deduplicator.Deduplicate(new[] { group });

        if (result.Count == 0) return true.ToProperty();

        var deduplicated = result[0];
        var highestWeightedRisk = group.Statements.Max(s => s.WeightedRisk);

        return (deduplicated.PrimaryStatement.WeightedRisk == highestWeightedRisk).ToProperty();
    }

    public static Arbitrary<StatementGroup> ArbitraryStatementGroup() => ArbitraryGroupWithDistinctWeights();

    #endregion

    #region Property 16: Work item ID uniqueness and format

    /// <summary>
    /// **Validates: Requirements 8.4**
    /// Property 16: For any generated work item collection of size N, all IDs SHALL be unique,
    /// match the pattern WI-\d{3,}, and form a sequential series starting at WI-001 through WI-N.
    /// </summary>
    [Property(MaxTest = 200, Arbitrary = new[] { typeof(WorkItemDeduplicatorPropertyTests) })]
    public Property AllIds_AreUnique_MatchFormat_AndSequential(IReadOnlyList<StatementGroup> groups)
    {
        var result = _deduplicator.Deduplicate(groups);

        Func<bool> allUnique = () => result.Select(r => r.Id).Distinct().Count() == result.Count;
        Func<bool> allMatchPattern = () => result.All(r => WorkItemIdPattern.IsMatch(r.Id));
        Func<bool> sequential = () =>
        {
            for (var i = 0; i < result.Count; i++)
            {
                var expectedId = $"WI-{(i + 1):D3}";
                if (result[i].Id != expectedId) return false;
            }
            return true;
        };

        return (result.Count == groups.Count).ToProperty()
            .And(allUnique().ToProperty())
            .And(allMatchPattern().ToProperty())
            .And(sequential().ToProperty());
    }

    /// <summary>
    /// **Validates: Requirements 8.4**
    /// Property 16 (format check): Every ID matches the regex ^WI-\d{3,}$.
    /// </summary>
    [Property(MaxTest = 200, Arbitrary = new[] { typeof(WorkItemDeduplicatorPropertyTests) })]
    public Property AllIds_MatchWiRegexPattern(IReadOnlyList<StatementGroup> groups)
    {
        var result = _deduplicator.Deduplicate(groups);

        return result.All(r => WorkItemIdPattern.IsMatch(r.Id)).ToProperty();
    }

    #endregion

    #region Property 17: Cross-references for shared objects

    /// <summary>
    /// **Validates: Requirements 8.5**
    /// Property 17: For any database object that appears in K > 1 work items,
    /// each of those K work items SHALL contain RelatedWorkItemIds listing the IDs
    /// of the other (K-1) work items sharing that object.
    /// </summary>
    [Property(MaxTest = 200, Arbitrary = new[] { typeof(WorkItemDeduplicatorPropertyTests) })]
    public Property SharedObjects_HaveCorrectCrossReferences(IReadOnlyList<StatementGroup> groups)
    {
        var result = _deduplicator.Deduplicate(groups);

        // Build expected cross-reference map: object name → list of work item IDs
        var objectToIds = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var group in result)
        {
            var objName = group.Group.DatabaseObjectName;
            if (objName is null) continue;

            if (!objectToIds.TryGetValue(objName, out var ids))
            {
                ids = new List<string>();
                objectToIds[objName] = ids;
            }
            ids.Add(group.Id);
        }

        // For each object appearing in K>1 work items, verify each has K-1 related IDs
        Func<bool> crossRefsCorrect = () =>
        {
            foreach (var (objectName, ids) in objectToIds)
            {
                if (ids.Count <= 1) continue;

                foreach (var id in ids)
                {
                    var group = result.First(r => r.Id == id);
                    var expectedRelated = ids.Where(x => x != id).OrderBy(x => x).ToList();
                    var actualRelated = group.RelatedWorkItemIds.OrderBy(x => x).ToList();

                    if (!expectedRelated.SequenceEqual(actualRelated))
                        return false;
                }
            }
            return true;
        };

        return crossRefsCorrect().ToProperty();
    }

    /// <summary>
    /// **Validates: Requirements 8.5**
    /// Property 17 (count check): Each shared-object work item has exactly K-1 related IDs.
    /// </summary>
    [Property(MaxTest = 200, Arbitrary = new[] { typeof(WorkItemDeduplicatorPropertyTests) })]
    public Property SharedObjects_HaveExactlyKMinus1RelatedIds(IReadOnlyList<StatementGroup> groups)
    {
        var result = _deduplicator.Deduplicate(groups);

        // Build map: object name → count of work items referencing it
        var objectToCount = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var group in result)
        {
            var objName = group.Group.DatabaseObjectName;
            if (objName is null) continue;

            objectToCount.TryGetValue(objName, out var count);
            objectToCount[objName] = count + 1;
        }

        Func<bool> countsCorrect = () =>
        {
            foreach (var group in result)
            {
                var objName = group.Group.DatabaseObjectName;
                if (objName is null)
                {
                    // Ad hoc groups should have no related IDs
                    if (group.RelatedWorkItemIds.Count != 0) return false;
                    continue;
                }

                var k = objectToCount[objName];
                if (k > 1)
                {
                    if (group.RelatedWorkItemIds.Count != k - 1) return false;
                }
                else
                {
                    if (group.RelatedWorkItemIds.Count != 0) return false;
                }
            }
            return true;
        };

        return countsCorrect().ToProperty();
    }

    public static Arbitrary<IReadOnlyList<StatementGroup>> ArbitraryIReadOnlyListStatementGroup() =>
        ArbitraryGroupsWithSharedObjects();

    #endregion

    #region Property 2: No duplicate SqlServerPattern across work items

    /// <summary>
    /// **Validates: Requirements 9.4**
    /// Property 2: For any valid set of analyzed statements and feature detection results,
    /// no two work items in the output SHALL have the same SqlServerPattern string value.
    /// </summary>
    [Property(MaxTest = 200, Arbitrary = new[] { typeof(WorkItemDeduplicatorPropertyTests) })]
    public Property NoDuplicateSqlServerPattern_AcrossWorkItems(IReadOnlyList<StatementGroup> groups)
    {
        // Run through the deduplicator (which assigns PrimarySqlPattern, the source of SqlServerPattern)
        var result = _deduplicator.Deduplicate(groups);

        if (result.Count <= 1)
            return true.ToProperty();

        // The SqlServerPattern on the final WorkItem comes directly from PrimarySqlPattern
        // Verify no two deduplicated groups share the same PrimarySqlPattern
        var sqlPatterns = result.Select(r => r.PrimarySqlPattern).ToList();
        var distinctCount = sqlPatterns.Distinct(StringComparer.Ordinal).Count();

        return (distinctCount == sqlPatterns.Count).ToProperty();
    }

    /// <summary>
    /// **Validates: Requirements 9.4**
    /// Property 2 (end-to-end): For any valid set of analyzed statements with distinct SQL texts,
    /// the full pipeline produces work items with unique SqlServerPattern values.
    /// This verifies the invariant through the real StatementGrouper → WorkItemDeduplicator flow.
    /// </summary>
    [Property(MaxTest = 100)]
    public Property NoDuplicateSqlServerPattern_EndToEnd_ThroughGrouper()
    {
        var grouper = new StatementGrouper(
            Microsoft.Extensions.Logging.Abstractions.NullLogger<StatementGrouper>.Instance);

        // Generate statements with distinct SQL texts to ensure realistic pipeline input
        var gen = from count in Gen.Choose(2, 10)
                  from statements in Gen.ListOf(count, GenStatementWithUniqueSql())
                  select statements.ToList();

        return Prop.ForAll(gen.ToArbitrary(), statements =>
        {
            var featureDetection = new FeatureDetectionResult
            {
                FeatureCounts = new Dictionary<string, int>(),
                DetailedInventory = Array.Empty<DetectedServerFeature>(),
                InaccessibleFeatures = Array.Empty<InaccessibleFeature>()
            };

            // Run the grouper
            var groups = grouper.GroupStatements(
                statements,
                featureDetection,
                minimumRiskLevel: 1);

            // Run the deduplicator
            var deduplicated = _deduplicator.Deduplicate(groups);

            if (deduplicated.Count <= 1) return;

            // Verify no duplicate SqlServerPattern (PrimarySqlPattern)
            var sqlPatterns = deduplicated.Select(r => r.PrimarySqlPattern).ToList();
            var distinctCount = sqlPatterns.Distinct(StringComparer.Ordinal).Count();

            distinctCount.Should().Be(sqlPatterns.Count,
                "no two work items should share the same SqlServerPattern value");
        });
    }

    /// <summary>
    /// Generates an AnalyzedStatement with a unique SQL text based on a GUID.
    /// Ensures each generated statement has a distinct SqlText for realistic pipeline testing.
    /// </summary>
    private static Gen<AnalyzedStatement> GenStatementWithUniqueSql()
    {
        var features = new[] { "TOP", "ISNULL", "MERGE", "TRY_CATCH", "XML_METHOD" };

        return from featureIndex in Gen.Choose(0, features.Length - 1)
               from objectIndex in Gen.Choose(0, 5)
               from uniqueId in Arb.Generate<Guid>()
               from weightedRisk in Arb.Generate<PositiveInt>().Select(p => (double)p.Get)
               let featureName = features[featureIndex]
               let sqlText = $"SELECT {featureName}_{uniqueId:N} FROM dbo.Table{objectIndex}"
               select new AnalyzedStatement
               {
                   Source = new CollectedStatement
                   {
                       SqlText = sqlText,
                       Source = StatementSource.QueryStore,
                       QueryHash = Guid.NewGuid().ToString("N"),
                       ExecutionCount = 1
                   },
                   Classification = StatementClassification.Select,
                   Features = new[]
                   {
                       new DetectedFeature
                       {
                           FeatureName = featureName,
                           Category = FeatureCategory.QueryFeature,
                           StatementId = Guid.NewGuid().ToString(),
                           Line = 1,
                           Column = 1
                       }
                   },
                   RiskScore = 3,
                   WeightedRisk = weightedRisk
               };
    }

    #endregion
}
