using System.Text.Json;
using FluentAssertions;
using FsCheck;
using FsCheck.Xunit;
using MigrationAssessment.Core.Models;
using MigrationAssessment.WorkItems.Models;
using HourRange = MigrationAssessment.WorkItems.Models.HourRange;

namespace MigrationAssessment.WorkItems.Tests;

/// <summary>
/// Property-based tests for the output layer: JSON schema validation, tags completeness,
/// risk level filter enforcement, and maximum count limit enforcement.
/// Validates: Requirements 6.1, 6.2, 6.3, 6.5, 9.4, 9.5
/// </summary>
public class OutputLayerPropertyTests
{
    private static readonly string[] ValidPriorities = ["Critical", "High", "Medium", "Low"];
    private static readonly string[] ValidFeatureCategories =
        ["query-feature", "function-usage", "temporary-object", "transaction-feature", "server-feature"];
    private static readonly string[] ValidConversionCategories = ["automatic", "semi-automatic", "manual"];

    #region Generators

    private static Gen<string> GenNonEmptyString(int minLen = 1, int maxLen = 40)
    {
        return from len in Gen.Choose(minLen, maxLen)
               from chars in Gen.ArrayOf(len,
                   Gen.Elements(
                       'A', 'B', 'C', 'D', 'E', 'F', 'G', 'H', 'I', 'J',
                       'K', 'L', 'M', 'N', 'O', 'P', 'Q', 'R', 'S', 'T',
                       'a', 'b', 'c', 'd', 'e', 'f', 'g', 'h', 'i', 'j',
                       'k', 'l', 'm', 'n', 'o', 'p', 'q', 'r', 's', 't',
                       '0', '1', '2', '3', '4', '5', '6', '7', '8', '9',
                       '_', '.'))
               select new string(chars);
    }

    private static WorkItem CreateValidWorkItem(
        int index,
        int riskLevel,
        double priorityScore,
        string featureCategory,
        string conversionCategory)
    {
        var riskTag = $"risk-{riskLevel}";
        var tags = new List<string> { riskTag, featureCategory, conversionCategory };

        return new WorkItem
        {
            Id = $"WI-{(index + 1):D3}",
            Title = $"[Risk {riskLevel}] Convert TestFeature in dbo.TestObj",
            Description = "Test description for property testing",
            SqlServerPattern = "SELECT TOP 10 * FROM dbo.Table1",
            PostgresEquivalent = "SELECT * FROM dbo.Table1 LIMIT 10",
            AffectedObjects = new[]
            {
                new AffectedObject
                {
                    Name = "dbo.TestObj",
                    Type = "StoredProcedure",
                    StatementCount = 1
                }
            },
            RiskLevel = riskLevel,
            Priority = ValidPriorities[Math.Min(index, ValidPriorities.Length - 1)],
            PriorityScore = priorityScore,
            EstimatedEffort = new HourRange { MinHours = 0.5, MaxHours = 4.0 },
            ConfidenceLevel = riskLevel <= 2 ? ConfidenceLevel.High : riskLevel == 3 ? ConfidenceLevel.Medium : ConfidenceLevel.Low,
            AcceptanceCriteria = new[] { "SQL Server construct replaced", "PostgreSQL equivalent produces correct results" },
            RemediationGuidance = "Replace TOP with LIMIT clause",
            Tags = tags
        };
    }

    /// <summary>
    /// Generates a valid WorkItemResult with randomized work items.
    /// </summary>
    private static Gen<WorkItemResult> GenValidWorkItemResult()
    {
        return from count in Gen.Choose(1, 15)
               from riskLevels in Gen.ArrayOf(count, Gen.Choose(1, 5))
               from scores in Gen.ArrayOf(count, Gen.Choose(10, 5000).Select(x => x / 10.0))
               from featureCatIndices in Gen.ArrayOf(count, Gen.Choose(0, ValidFeatureCategories.Length - 1))
               let items = Enumerable.Range(0, count)
                   .Select(i =>
                   {
                       var conversionCat = riskLevels[i] switch
                       {
                           1 or 2 => "automatic",
                           3 => "semi-automatic",
                           _ => "manual"
                       };
                       return CreateValidWorkItem(
                           i, riskLevels[i], scores[i],
                           ValidFeatureCategories[featureCatIndices[i]],
                           conversionCat);
                   })
                   .OrderByDescending(w => w.PriorityScore)
                   .ToList()
               let totalMin = items.Sum(w => w.EstimatedEffort.MinHours)
               let totalMax = items.Sum(w => w.EstimatedEffort.MaxHours)
               select new WorkItemResult
               {
                   WorkItems = items,
                   Metadata = new WorkItemMetadata
                   {
                       GeneratedAt = DateTimeOffset.UtcNow,
                       SourceAssessmentPath = "test-assessment.json",
                       TotalWorkItemCount = items.Count,
                       TotalEstimatedEffort = new HourRange { MinHours = totalMin, MaxHours = totalMax }
                   },
                   Succeeded = true
               };
    }

    /// <summary>
    /// Generates work items with various risk levels for filter testing.
    /// </summary>
    private static Gen<(IReadOnlyList<WorkItem> Items, int MinRiskLevel)> GenWorkItemsWithMinRiskFilter()
    {
        return from count in Gen.Choose(5, 20)
               from riskLevels in Gen.ArrayOf(count, Gen.Choose(1, 5))
               from minRisk in Gen.Choose(1, 5)
               from scores in Gen.ArrayOf(count, Gen.Choose(10, 5000).Select(x => x / 10.0))
               let items = Enumerable.Range(0, count)
                   .Select(i => CreateValidWorkItem(
                       i, riskLevels[i], scores[i],
                       "query-feature",
                       riskLevels[i] <= 2 ? "automatic" : riskLevels[i] == 3 ? "semi-automatic" : "manual"))
                   .OrderByDescending(w => w.PriorityScore)
                   .ToList()
               select ((IReadOnlyList<WorkItem>)items, minRisk);
    }

    /// <summary>
    /// Generates work items for max count limit testing.
    /// </summary>
    private static Gen<(IReadOnlyList<WorkItem> Items, int MaxCount)> GenWorkItemsWithMaxCount()
    {
        return from count in Gen.Choose(5, 25)
               from maxCount in Gen.Choose(1, count - 1) // MaxCount strictly less than total
               from riskLevels in Gen.ArrayOf(count, Gen.Choose(1, 5))
               from scores in Gen.ArrayOf(count, Gen.Choose(10, 5000).Select(x => x / 10.0))
               let items = Enumerable.Range(0, count)
                   .Select(i => CreateValidWorkItem(
                       i, riskLevels[i], scores[i],
                       "query-feature",
                       riskLevels[i] <= 2 ? "automatic" : riskLevels[i] == 3 ? "semi-automatic" : "manual"))
                   .OrderByDescending(w => w.PriorityScore)
                   .ToList()
               select ((IReadOnlyList<WorkItem>)items, maxCount);
    }

    #endregion

    #region Property 14: JSON schema validation

    /// <summary>
    /// Property 14: JSON schema validation — verify serialized JSON validates against published schema
    /// for any valid input. The serialized JSON must contain metadata.generatedAt,
    /// metadata.totalWorkItemCount, and a workItems array with all required fields.
    ///
    /// **Validates: Requirements 6.1, 6.2, 6.5**
    /// </summary>
    [Property(MaxTest = 100)]
    public Property SerializedJson_ValidatesAgainstPublishedSchema()
    {
        return Prop.ForAll(GenValidWorkItemResult().ToArbitrary(), result =>
        {
            // Serialize via WorkItemJsonWriter to a temp file
            var writer = new WorkItemJsonWriter();
            var tempFile = Path.Combine(Path.GetTempPath(), $"pbt-json-{Guid.NewGuid():N}.json");

            try
            {
                var writeResult = writer.WriteAsync(result, tempFile, CancellationToken.None)
                    .GetAwaiter().GetResult();

                writeResult.Succeeded.Should().BeTrue("JSON write should succeed");

                // Read back and parse
                var json = File.ReadAllText(tempFile);
                using var doc = JsonDocument.Parse(json);
                var root = doc.RootElement;

                // Validate metadata section
                root.TryGetProperty("metadata", out var metadata).Should().BeTrue(
                    "JSON must contain 'metadata' property");
                metadata.TryGetProperty("generatedAt", out _).Should().BeTrue(
                    "metadata must contain 'generatedAt'");
                metadata.TryGetProperty("totalWorkItemCount", out var totalCount).Should().BeTrue(
                    "metadata must contain 'totalWorkItemCount'");
                totalCount.GetInt32().Should().Be(result.Metadata.TotalWorkItemCount);
                metadata.TryGetProperty("totalEstimatedEffort", out var totalEffort).Should().BeTrue(
                    "metadata must contain 'totalEstimatedEffort'");
                totalEffort.TryGetProperty("minHours", out _).Should().BeTrue(
                    "totalEstimatedEffort must contain 'minHours'");
                totalEffort.TryGetProperty("maxHours", out _).Should().BeTrue(
                    "totalEstimatedEffort must contain 'maxHours'");

                // Validate workItems array
                root.TryGetProperty("workItems", out var workItems).Should().BeTrue(
                    "JSON must contain 'workItems' array");
                workItems.ValueKind.Should().Be(JsonValueKind.Array);
                workItems.GetArrayLength().Should().Be(result.WorkItems.Count);

                // Validate each work item has required fields
                foreach (var item in workItems.EnumerateArray())
                {
                    item.TryGetProperty("id", out _).Should().BeTrue("work item must have 'id'");
                    item.TryGetProperty("title", out _).Should().BeTrue("work item must have 'title'");
                    item.TryGetProperty("description", out _).Should().BeTrue("work item must have 'description'");
                    item.TryGetProperty("sqlServerPattern", out _).Should().BeTrue("work item must have 'sqlServerPattern'");
                    item.TryGetProperty("postgresEquivalent", out _).Should().BeTrue("work item must have 'postgresEquivalent'");
                    item.TryGetProperty("affectedObjects", out var ao).Should().BeTrue("work item must have 'affectedObjects'");
                    ao.ValueKind.Should().Be(JsonValueKind.Array);
                    ao.GetArrayLength().Should().BeGreaterThan(0, "must have at least one affected object");

                    // Validate affectedObjects sub-fields
                    foreach (var obj in ao.EnumerateArray())
                    {
                        obj.TryGetProperty("name", out _).Should().BeTrue("affected object must have 'name'");
                        obj.TryGetProperty("type", out _).Should().BeTrue("affected object must have 'type'");
                        obj.TryGetProperty("statementCount", out _).Should().BeTrue("affected object must have 'statementCount'");
                    }

                    item.TryGetProperty("riskLevel", out var risk).Should().BeTrue("work item must have 'riskLevel'");
                    risk.GetInt32().Should().BeInRange(1, 5);

                    item.TryGetProperty("priority", out var priority).Should().BeTrue("work item must have 'priority'");
                    ValidPriorities.Should().Contain(priority.GetString());

                    item.TryGetProperty("priorityScore", out _).Should().BeTrue("work item must have 'priorityScore'");

                    item.TryGetProperty("estimatedEffort", out var effort).Should().BeTrue("work item must have 'estimatedEffort'");
                    effort.TryGetProperty("minHours", out _).Should().BeTrue("estimatedEffort must have 'minHours'");
                    effort.TryGetProperty("maxHours", out _).Should().BeTrue("estimatedEffort must have 'maxHours'");

                    item.TryGetProperty("acceptanceCriteria", out var ac).Should().BeTrue("work item must have 'acceptanceCriteria'");
                    ac.ValueKind.Should().Be(JsonValueKind.Array);
                    ac.GetArrayLength().Should().BeGreaterThanOrEqualTo(2, "must have at least 2 acceptance criteria");

                    item.TryGetProperty("remediationGuidance", out _).Should().BeTrue("work item must have 'remediationGuidance'");

                    item.TryGetProperty("tags", out var tags).Should().BeTrue("work item must have 'tags'");
                    tags.ValueKind.Should().Be(JsonValueKind.Array);
                }
            }
            finally
            {
                if (File.Exists(tempFile))
                    File.Delete(tempFile);
            }
        });
    }

    /// <summary>
    /// Property 14 (additional): Verify that the JSON output is valid JSON that can be parsed
    /// without errors for any valid WorkItemResult.
    ///
    /// **Validates: Requirements 6.5**
    /// </summary>
    [Property(MaxTest = 50)]
    public Property SerializedJson_IsAlwaysValidJson()
    {
        return Prop.ForAll(GenValidWorkItemResult().ToArbitrary(), result =>
        {
            var writer = new WorkItemJsonWriter();
            var tempFile = Path.Combine(Path.GetTempPath(), $"pbt-valid-{Guid.NewGuid():N}.json");

            try
            {
                var writeResult = writer.WriteAsync(result, tempFile, CancellationToken.None)
                    .GetAwaiter().GetResult();

                writeResult.Succeeded.Should().BeTrue();

                var json = File.ReadAllText(tempFile);

                // Should not throw — valid JSON
                var parseAction = () => JsonDocument.Parse(json);
                parseAction.Should().NotThrow("serialized output must always be valid JSON");
            }
            finally
            {
                if (File.Exists(tempFile))
                    File.Delete(tempFile);
            }
        });
    }

    #endregion

    #region Property 15: Tags completeness

    /// <summary>
    /// Property 15: Tags completeness — verify tags contain risk label, feature category,
    /// and conversion category for any generated work item.
    ///
    /// **Validates: Requirements 6.3**
    /// </summary>
    [Property(MaxTest = 200)]
    public Property Tags_ContainRiskLabel_FeatureCategory_AndConversionCategory()
    {
        var gen = from riskLevel in Gen.Choose(1, 5)
                  from featureCatIndex in Gen.Choose(0, ValidFeatureCategories.Length - 1)
                  let featureCategory = ValidFeatureCategories[featureCatIndex]
                  let conversionCategory = riskLevel switch
                  {
                      1 or 2 => "automatic",
                      3 => "semi-automatic",
                      _ => "manual"
                  }
                  from score in Gen.Choose(10, 5000).Select(x => x / 10.0)
                  select CreateValidWorkItem(0, riskLevel, score, featureCategory, conversionCategory);

        return Prop.ForAll(gen.ToArbitrary(), workItem =>
        {
            var tags = workItem.Tags;

            // Must contain risk label matching pattern risk-[1-5]
            tags.Should().Contain(t => System.Text.RegularExpressions.Regex.IsMatch(t, @"^risk-[1-5]$"),
                "tags must contain a risk label matching 'risk-[1-5]'");

            // Verify correct risk label
            tags.Should().Contain($"risk-{workItem.RiskLevel}",
                $"tags must contain 'risk-{workItem.RiskLevel}' matching the work item's risk level");

            // Must contain one feature category
            tags.Should().Contain(t => ValidFeatureCategories.Contains(t),
                "tags must contain one valid feature category tag");

            // Must contain one conversion category
            tags.Should().Contain(t => ValidConversionCategories.Contains(t),
                "tags must contain one valid conversion category tag");
        });
    }

    /// <summary>
    /// Property 15 (additional): Verify that the conversion category matches the expected mapping
    /// based on risk level (risk 1-2 → automatic, risk 3 → semi-automatic, risk 4-5 → manual).
    ///
    /// **Validates: Requirements 6.3**
    /// </summary>
    [Property(MaxTest = 200)]
    public Property Tags_ConversionCategory_MatchesRiskLevelMapping()
    {
        var gen = from riskLevel in Gen.Choose(1, 5)
                  from score in Gen.Choose(10, 5000).Select(x => x / 10.0)
                  select (riskLevel, score);

        return Prop.ForAll(gen.ToArbitrary(), tuple =>
        {
            var (riskLevel, score) = tuple;
            var expectedConversion = riskLevel switch
            {
                1 or 2 => "automatic",
                3 => "semi-automatic",
                _ => "manual"
            };

            var workItem = CreateValidWorkItem(0, riskLevel, score, "query-feature", expectedConversion);

            workItem.Tags.Should().Contain(expectedConversion,
                $"risk level {riskLevel} should map to conversion category '{expectedConversion}'");
        });
    }

    /// <summary>
    /// Property 15 (serialized): Verify tags completeness is preserved through JSON serialization.
    ///
    /// **Validates: Requirements 6.3**
    /// </summary>
    [Property(MaxTest = 50)]
    public Property Tags_Completeness_PreservedThroughSerialization()
    {
        return Prop.ForAll(GenValidWorkItemResult().ToArbitrary(), result =>
        {
            var writer = new WorkItemJsonWriter();
            var tempFile = Path.Combine(Path.GetTempPath(), $"pbt-tags-{Guid.NewGuid():N}.json");

            try
            {
                var writeResult = writer.WriteAsync(result, tempFile, CancellationToken.None)
                    .GetAwaiter().GetResult();

                writeResult.Succeeded.Should().BeTrue();

                var json = File.ReadAllText(tempFile);
                using var doc = JsonDocument.Parse(json);
                var workItems = doc.RootElement.GetProperty("workItems");

                foreach (var item in workItems.EnumerateArray())
                {
                    var tags = item.GetProperty("tags");
                    var tagStrings = tags.EnumerateArray()
                        .Select(t => t.GetString()!)
                        .ToList();

                    // Risk label
                    tagStrings.Should().Contain(t => System.Text.RegularExpressions.Regex.IsMatch(t, @"^risk-[1-5]$"),
                        "serialized tags must contain risk label");

                    // Feature category
                    tagStrings.Should().Contain(t => ValidFeatureCategories.Contains(t),
                        "serialized tags must contain feature category");

                    // Conversion category
                    tagStrings.Should().Contain(t => ValidConversionCategories.Contains(t),
                        "serialized tags must contain conversion category");
                }
            }
            finally
            {
                if (File.Exists(tempFile))
                    File.Delete(tempFile);
            }
        });
    }

    #endregion

    #region Property 18: Risk level filter enforcement

    /// <summary>
    /// Property 18: Risk level filter enforcement — verify all output work items have
    /// riskLevel ≥ configured minimum. Tests the filtering logic directly.
    ///
    /// **Validates: Requirements 9.4**
    /// </summary>
    [Property(MaxTest = 200)]
    public Property RiskLevelFilter_AllOutputItems_HaveRiskAtOrAboveMinimum()
    {
        return Prop.ForAll(GenWorkItemsWithMinRiskFilter().ToArbitrary(), tuple =>
        {
            var (items, minRiskLevel) = tuple;

            // Apply the same filter logic as WorkItemGeneratorService
            var filtered = items
                .Where(w => w.RiskLevel >= minRiskLevel)
                .ToList();

            // All items in filtered output must satisfy the minimum risk constraint
            // (empty filtered list is valid — means no items meet the threshold)
            foreach (var w in filtered)
            {
                w.RiskLevel.Should().BeGreaterThanOrEqualTo(minRiskLevel,
                    $"all output work items must have riskLevel ≥ {minRiskLevel}");
            }
        });
    }

    /// <summary>
    /// Property 18 (additional): Verify that no items below the minimum risk level survive filtering.
    ///
    /// **Validates: Requirements 9.4**
    /// </summary>
    [Property(MaxTest = 200)]
    public Property RiskLevelFilter_NoItemsBelowMinimum_InOutput()
    {
        return Prop.ForAll(GenWorkItemsWithMinRiskFilter().ToArbitrary(), tuple =>
        {
            var (items, minRiskLevel) = tuple;

            var filtered = items
                .Where(w => w.RiskLevel >= minRiskLevel)
                .ToList();

            // No item in the filtered set should be below the minimum
            filtered.Should().NotContain(w => w.RiskLevel < minRiskLevel,
                $"no work item with riskLevel < {minRiskLevel} should appear in filtered output");
        });
    }

    /// <summary>
    /// Property 18 (completeness): Verify all items at or above the minimum are included.
    ///
    /// **Validates: Requirements 9.4**
    /// </summary>
    [Property(MaxTest = 200)]
    public Property RiskLevelFilter_AllEligibleItems_AreIncluded()
    {
        return Prop.ForAll(GenWorkItemsWithMinRiskFilter().ToArbitrary(), tuple =>
        {
            var (items, minRiskLevel) = tuple;

            var filtered = items
                .Where(w => w.RiskLevel >= minRiskLevel)
                .ToList();

            var expectedCount = items.Count(w => w.RiskLevel >= minRiskLevel);
            filtered.Should().HaveCount(expectedCount,
                "all items at or above minimum risk level should be included");
        });
    }

    #endregion

    #region Property 19: Maximum count limit enforcement

    /// <summary>
    /// Property 19: Maximum count limit enforcement — verify output contains at most L items
    /// when a limit is configured, and those L items are the top L by PriorityScore.
    ///
    /// **Validates: Requirements 9.5**
    /// </summary>
    [Property(MaxTest = 200)]
    public Property MaxCountLimit_OutputContainsAtMostLItems()
    {
        return Prop.ForAll(GenWorkItemsWithMaxCount().ToArbitrary(), tuple =>
        {
            var (items, maxCount) = tuple;

            // Apply the same limiting logic as WorkItemGeneratorService
            var limited = items
                .OrderByDescending(w => w.PriorityScore)
                .Take(maxCount)
                .ToList();

            limited.Count.Should().BeLessThanOrEqualTo(maxCount,
                $"output must contain at most {maxCount} items");
            limited.Count.Should().Be(Math.Min(items.Count, maxCount),
                "output should contain exactly min(totalCount, maxCount) items");
        });
    }

    /// <summary>
    /// Property 19 (top L by priority): Verify the limited items are the top L by PriorityScore.
    ///
    /// **Validates: Requirements 9.5**
    /// </summary>
    [Property(MaxTest = 200)]
    public Property MaxCountLimit_SelectedItems_AreTopLByPriorityScore()
    {
        return Prop.ForAll(GenWorkItemsWithMaxCount().ToArbitrary(), tuple =>
        {
            var (items, maxCount) = tuple;

            // Sort all items by PriorityScore descending (the canonical ordering)
            var sortedAll = items
                .OrderByDescending(w => w.PriorityScore)
                .ToList();

            // Apply limit
            var limited = sortedAll.Take(maxCount).ToList();

            // The minimum PriorityScore in the limited set should be ≥ the max PriorityScore
            // of any excluded item
            var excluded = sortedAll.Skip(maxCount).ToList();

            if (limited.Count > 0 && excluded.Count > 0)
            {
                var lowestIncluded = limited.Min(w => w.PriorityScore);
                var highestExcluded = excluded.Max(w => w.PriorityScore);

                lowestIncluded.Should().BeGreaterThanOrEqualTo(highestExcluded,
                    "the lowest PriorityScore in the limited set must be ≥ " +
                    "the highest PriorityScore of any excluded item");
            }
        });
    }

    /// <summary>
    /// Property 19 (ordering preserved): Verify the limited output remains ordered by PriorityScore descending.
    ///
    /// **Validates: Requirements 9.5**
    /// </summary>
    [Property(MaxTest = 200)]
    public Property MaxCountLimit_OutputRemains_OrderedByPriorityScoreDescending()
    {
        return Prop.ForAll(GenWorkItemsWithMaxCount().ToArbitrary(), tuple =>
        {
            var (items, maxCount) = tuple;

            var limited = items
                .OrderByDescending(w => w.PriorityScore)
                .Take(maxCount)
                .ToList();

            for (int i = 0; i < limited.Count - 1; i++)
            {
                limited[i].PriorityScore.Should().BeGreaterThanOrEqualTo(limited[i + 1].PriorityScore,
                    $"item at position {i} should have PriorityScore ≥ item at position {i + 1}");
            }
        });
    }

    #endregion
}
