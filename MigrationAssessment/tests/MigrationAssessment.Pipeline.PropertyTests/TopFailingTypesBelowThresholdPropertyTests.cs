using FluentAssertions;
using FsCheck;
using FsCheck.Xunit;

namespace MigrationAssessment.Pipeline.PropertyTests;

/// <summary>
/// Feature: migration-validation-pipeline, Property 6: Top Failing Types Below Threshold
/// 
/// Validates: Requirements 3.4
/// 
/// For any set of results where the aggregate Compatibility_Score is below 70%,
/// the Scoring Report SHALL include up to 5 failing object types ranked in descending
/// order by failure count, and each entry SHALL contain the correct failure count for that type.
/// </summary>
public class TopFailingTypesBelowThresholdPropertyTests
{
    private static readonly string[] ValidObjectTypes = { "Table", "View", "StoredProcedure", "Function", "Trigger" };

    #region Generators

    /// <summary>
    /// Generator that produces object result sets guaranteed to have an aggregate score below 70%.
    /// Strategy: generate 1-3 pass objects and 4-20 fail objects to guarantee &lt; 70%.
    /// </summary>
    private static Gen<List<ObjectResult>> GenBelowThresholdResults()
    {
        return from passCount in Gen.Choose(1, 3)
               from failCount in Gen.Choose(4, 20)
               from skipCount in Gen.Choose(0, 3)
               from objects in GenObjectResults(passCount, failCount, skipCount)
               select objects;
    }

    /// <summary>
    /// Generator that produces object result sets with aggregate score >= 70%.
    /// Strategy: generate 7-15 pass objects and 1-3 fail objects.
    /// </summary>
    private static Gen<List<ObjectResult>> GenAboveThresholdResults()
    {
        return from passCount in Gen.Choose(7, 15)
               from failCount in Gen.Choose(1, 3)
               from skipCount in Gen.Choose(0, 3)
               from objects in GenObjectResults(passCount, failCount, skipCount)
               select objects;
    }

    private static Gen<List<ObjectResult>> GenObjectResults(int passCount, int failCount, int skipCount)
    {
        return from passObjects in Gen.ListOf(passCount, GenObject(ObjectStatus.Pass))
               from failObjects in Gen.ListOf(failCount, GenFailObject())
               from skipObjects in Gen.ListOf(skipCount, GenObject(ObjectStatus.Skip))
               select passObjects.Concat(failObjects).Concat(skipObjects).ToList();
    }

    private static Gen<ObjectResult> GenObject(ObjectStatus status)
    {
        return from typeIdx in Gen.Choose(0, ValidObjectTypes.Length - 1)
               from dbIdx in Gen.Choose(1, 3)
               from objIdx in Gen.Choose(1, 100)
               select new ObjectResult(
                   $"dbo.obj_{objIdx}",
                   ValidObjectTypes[typeIdx],
                   $"TestDB{dbIdx}",
                   status
               );
    }

    private static Gen<ObjectResult> GenFailObject()
    {
        return from typeIdx in Gen.Choose(0, ValidObjectTypes.Length - 1)
               from dbIdx in Gen.Choose(1, 3)
               from objIdx in Gen.Choose(1, 100)
               from failType in Gen.Elements(ObjectStatus.FailSyntax, ObjectStatus.FailConvert)
               select new ObjectResult(
                   $"dbo.obj_{objIdx}",
                   ValidObjectTypes[typeIdx],
                   $"TestDB{dbIdx}",
                   failType
               );
    }

    #endregion

    /// <summary>
    /// Property: When aggregate score is below 70%, the top failing types list
    /// contains at most 5 entries.
    /// </summary>
    [Property(MaxTest = 100)]
    public Property AtMost5FailingTypesWhenBelowThreshold()
    {
        return Prop.ForAll(GenBelowThresholdResults().ToArbitrary(), (List<ObjectResult> results) =>
        {
            var topFailing = ScoringEngine.ComputeTopFailingTypes(results);

            topFailing.Count.Should().BeGreaterThan(0, "there should be at least one failing type when score < 70%");
            topFailing.Count.Should().BeLessOrEqualTo(5, "at most 5 types should be listed");
        });
    }

    /// <summary>
    /// Property: Types are ranked by failure count in descending order.
    /// </summary>
    [Property(MaxTest = 100)]
    public Property FailingTypesAreRankedByFailCountDescending()
    {
        return Prop.ForAll(GenBelowThresholdResults().ToArbitrary(), (List<ObjectResult> results) =>
        {
            var topFailing = ScoringEngine.ComputeTopFailingTypes(results);

            if (topFailing.Count > 1)
            {
                for (int i = 0; i < topFailing.Count - 1; i++)
                {
                    topFailing[i].FailCount.Should().BeGreaterThanOrEqualTo(
                        topFailing[i + 1].FailCount,
                        $"entry at index {i} should have >= fail count than entry at index {i + 1}");
                }
            }
        });
    }

    /// <summary>
    /// Property: Each entry contains the correct failure count for that type.
    /// The reported failCount matches the actual number of failed objects of that type.
    /// </summary>
    [Property(MaxTest = 100)]
    public Property EachEntryHasCorrectFailureCount()
    {
        return Prop.ForAll(GenBelowThresholdResults().ToArbitrary(), (List<ObjectResult> results) =>
        {
            var topFailing = ScoringEngine.ComputeTopFailingTypes(results);

            // Compute expected failure counts per type
            var expectedCounts = results
                .Where(o => o.Status == ObjectStatus.FailSyntax || o.Status == ObjectStatus.FailConvert)
                .GroupBy(o => o.ObjectType)
                .ToDictionary(g => g.Key, g => g.Count());

            foreach (var entry in topFailing)
            {
                expectedCounts.Should().ContainKey(entry.Type);
                entry.FailCount.Should().Be(expectedCounts[entry.Type],
                    $"fail count for type '{entry.Type}' should match actual failures");
            }
        });
    }

    /// <summary>
    /// Property: When aggregate score >= 70%, the top failing types list is empty.
    /// </summary>
    [Property(MaxTest = 100)]
    public Property EmptyListWhenAboveThreshold()
    {
        return Prop.ForAll(GenAboveThresholdResults().ToArbitrary(), (List<ObjectResult> results) =>
        {
            var aggregate = ScoringEngine.ComputeAggregateScore(results);

            // Only assert when we actually produced a score >= 70%
            if (aggregate.CompatibilityScore is not null && aggregate.CompatibilityScore >= 70.0)
            {
                var topFailing = ScoringEngine.ComputeTopFailingTypes(results);
                topFailing.Should().BeEmpty("top failing types should be empty when aggregate >= 70%");
            }
        });
    }

    /// <summary>
    /// Property: The top failing types list only includes types that actually have failures.
    /// No entry with FailCount of 0 should ever appear.
    /// </summary>
    [Property(MaxTest = 100)]
    public Property AllEntriesHavePositiveFailCount()
    {
        return Prop.ForAll(GenBelowThresholdResults().ToArbitrary(), (List<ObjectResult> results) =>
        {
            var topFailing = ScoringEngine.ComputeTopFailingTypes(results);

            foreach (var entry in topFailing)
            {
                entry.FailCount.Should().BeGreaterThan(0,
                    $"type '{entry.Type}' should have a positive failure count");
            }
        });
    }
}
