using FluentAssertions;
using FsCheck;
using FsCheck.Xunit;

namespace MigrationAssessment.Pipeline.PropertyTests;

/// <summary>
/// Property 5: Per-Type Score Breakdown Consistency
/// 
/// Validates: Requirements 3.5
/// 
/// For any database result in the Scoring Report, the sum of pass and fail counts
/// across all object type breakdowns SHALL equal the total pass + fail-syntax + fail-convert
/// count for that database, and each per-type Compatibility_Score SHALL be correctly
/// computed from that type's pass and fail counts.
/// </summary>
[Trait("Feature", "migration-validation-pipeline")]
[Trait("Property", "5: Per-Type Score Breakdown Consistency")]
public class PerTypeScoreBreakdownConsistencyTests
{
    /// <summary>
    /// **Validates: Requirements 3.5**
    /// 
    /// Property: The sum of pass + fail counts across all per-type breakdowns equals
    /// the database's total pass + fail-syntax + fail-convert count.
    /// 
    /// Skip objects are excluded from type breakdowns (they are not convertible),
    /// so only pass, fail-syntax, and fail-convert objects with valid types contribute.
    /// </summary>
    [Property(MaxTest = 200, Arbitrary = new[] { typeof(PerTypeBreakdownArbitrary) })]
    public void Sum_of_per_type_counts_equals_database_totals(ObjectResult[] objects)
    {
        // Compute per-database score
        var dbScore = ScoringEngine.ComputePerDatabaseScores(objects);
        var score = dbScore["TestDB"];

        // Compute per-type breakdowns
        var breakdowns = ScoringEngine.ComputePerTypeBreakdown(objects);

        // Sum pass + fail across all type breakdowns
        int totalTypePass = breakdowns.Sum(b => b.Pass);
        int totalTypeFail = breakdowns.Sum(b => b.Fail);

        // The database totals (excluding skip)
        int dbConvertibleTotal = score.Pass + score.FailSyntax + score.FailConvert;

        // The sum of per-type pass + fail should equal the database total convertible count
        // because every convertible object (pass, fail-syntax, fail-convert) must have a valid type
        (totalTypePass + totalTypeFail).Should().Be(dbConvertibleTotal,
            "sum of per-type pass + fail counts must equal the database's total convertible object count");
    }

    /// <summary>
    /// **Validates: Requirements 3.5**
    /// 
    /// Property: Each per-type Compatibility_Score is computed as
    /// (type pass) / (type pass + type fail) * 100, rounded to 1 decimal place.
    /// </summary>
    [Property(MaxTest = 200, Arbitrary = new[] { typeof(PerTypeBreakdownArbitrary) })]
    public void Per_type_compatibility_score_is_correctly_computed(ObjectResult[] objects)
    {
        var breakdowns = ScoringEngine.ComputePerTypeBreakdown(objects);

        foreach (var breakdown in breakdowns)
        {
            int convertible = breakdown.Pass + breakdown.Fail;

            if (convertible == 0)
            {
                // When all objects of a type are skip, score should be N/A (null)
                breakdown.Score.Should().BeNull(
                    $"type {breakdown.ObjectType} has zero convertible objects, so score should be N/A");
            }
            else
            {
                double expectedScore = Math.Round((double)breakdown.Pass / convertible * 100, 1);
                breakdown.Score.Should().Be(expectedScore,
                    $"type {breakdown.ObjectType} score should be (pass={breakdown.Pass}) / (pass+fail={convertible}) * 100 = {expectedScore}");
            }
        }
    }

    /// <summary>
    /// **Validates: Requirements 3.5**
    /// 
    /// Property: Per-type pass counts sum to the database's total pass count.
    /// Per-type fail counts sum to the database's total fail-syntax + fail-convert count.
    /// This ensures no objects are lost or double-counted in the breakdown.
    /// </summary>
    [Property(MaxTest = 200, Arbitrary = new[] { typeof(PerTypeBreakdownArbitrary) })]
    public void Per_type_pass_and_fail_counts_match_database_totals_separately(ObjectResult[] objects)
    {
        var dbScore = ScoringEngine.ComputePerDatabaseScores(objects);
        var score = dbScore["TestDB"];
        var breakdowns = ScoringEngine.ComputePerTypeBreakdown(objects);

        int totalTypePass = breakdowns.Sum(b => b.Pass);
        int totalTypeFail = breakdowns.Sum(b => b.Fail);

        totalTypePass.Should().Be(score.Pass,
            "sum of per-type pass counts must equal the database's total pass count");
        totalTypeFail.Should().Be(score.FailSyntax + score.FailConvert,
            "sum of per-type fail counts must equal the database's total fail-syntax + fail-convert count");
    }

    /// <summary>
    /// **Validates: Requirements 3.5**
    /// 
    /// Property: With multiple object types present, each type's breakdown only includes
    /// objects of that specific type (no cross-contamination between types).
    /// </summary>
    [Property(MaxTest = 200, Arbitrary = new[] { typeof(PerTypeBreakdownArbitrary) })]
    public void Per_type_breakdown_counts_only_objects_of_that_type(ObjectResult[] objects)
    {
        var breakdowns = ScoringEngine.ComputePerTypeBreakdown(objects);

        foreach (var breakdown in breakdowns)
        {
            // Manually count objects of this type
            var typeObjects = objects.Where(o => o.ObjectType == breakdown.ObjectType).ToList();
            int expectedPass = typeObjects.Count(o => o.Status == ObjectStatus.Pass);
            int expectedFail = typeObjects.Count(o => o.Status == ObjectStatus.FailSyntax || o.Status == ObjectStatus.FailConvert);

            breakdown.Pass.Should().Be(expectedPass,
                $"type {breakdown.ObjectType} pass count should match manual count");
            breakdown.Fail.Should().Be(expectedFail,
                $"type {breakdown.ObjectType} fail count should match manual count");
        }
    }
}

/// <summary>
/// FsCheck Arbitrary provider for generating object result arrays for per-type breakdown tests.
/// Generates random objects with valid types (Table, View, StoredProcedure, Function, Trigger)
/// and various status distributions, all within a single "TestDB" database.
/// </summary>
public class PerTypeBreakdownArbitrary
{
    public static Arbitrary<ObjectResult[]> ArbitraryObjectResults()
    {
        var objectTypes = ScoringEngine.ValidObjectTypes;
        var statuses = new[] { ObjectStatus.Pass, ObjectStatus.FailSyntax, ObjectStatus.FailConvert, ObjectStatus.Skip };

        var genObjectResult = from objectType in Gen.Elements(objectTypes)
                              from status in Gen.Elements(statuses)
                              from id in Gen.Choose(1, 1000)
                              select new ObjectResult(
                                  $"dbo.Object_{id}",
                                  objectType,
                                  "TestDB",
                                  status
                              );

        // Generate between 1 and 50 objects per database, ensure at least one convertible
        var genResults = from size in Gen.Choose(1, 50)
                         from results in Gen.ArrayOf(size, genObjectResult)
                         from forcedType in Gen.Elements(objectTypes)
                         from forcedStatus in Gen.Elements(ObjectStatus.Pass, ObjectStatus.FailSyntax, ObjectStatus.FailConvert)
                         select results.Append(new ObjectResult(
                             "dbo.Forced_Convertible",
                             forcedType,
                             "TestDB",
                             forcedStatus
                         )).ToArray();

        return Arb.From(genResults);
    }
}
