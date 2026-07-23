using FluentAssertions;
using FsCheck;
using FsCheck.Xunit;

namespace MigrationAssessment.Pipeline.PropertyTests;

/// <summary>
/// Feature: migration-validation-pipeline, Property 3: Compatibility Score Computation
/// 
/// Validates: Requirements 3.1, 3.3, 3.6
///
/// Property 3: For any set of object results across one or more databases where at least one
/// object is classified as pass, fail-syntax, or fail-convert, the Compatibility_Score SHALL equal
/// (pass count) / (pass + fail-syntax + fail-convert) * 100 rounded to one decimal place,
/// computed both per-database and as an aggregate across all databases (excluding databases
/// where all objects are "skip").
/// </summary>
public class CompatibilityScorePropertyTests
{
    private static readonly string[] ValidObjectTypes = { "Table", "View", "StoredProcedure", "Function", "Trigger" };
    private static readonly string[] DatabaseNames = { "DB1", "DB2", "DB3" };

    #region Generators

    /// <summary>
    /// Generates a list of ObjectResults with at least one convertible object.
    /// </summary>
    private static Gen<List<ObjectResult>> GenObjectResultsWithConvertible()
    {
        var genSingleResult = from dbName in Gen.Elements(DatabaseNames)
                              from objType in Gen.Elements(ValidObjectTypes)
                              from status in Gen.Elements(ObjectStatus.Pass, ObjectStatus.FailSyntax, ObjectStatus.FailConvert, ObjectStatus.Skip)
                              from idx in Gen.Choose(1, 1000)
                              select new ObjectResult($"{dbName}.obj_{idx}", objType, dbName, status);

        return from size in Gen.Choose(1, 50)
               from results in Gen.ListOf(size, genSingleResult)
               // Force at least one convertible object
               from forcedStatus in Gen.Elements(ObjectStatus.Pass, ObjectStatus.FailSyntax, ObjectStatus.FailConvert)
               from forcedType in Gen.Elements(ValidObjectTypes)
               from forcedDb in Gen.Elements(DatabaseNames)
               select results.Concat(new[]
               {
                   new ObjectResult($"{forcedDb}.forced_convertible", forcedType, forcedDb, forcedStatus)
               }).ToList();
    }

    /// <summary>
    /// Generates a multi-database result set where one database has only "skip" objects
    /// and another has at least one convertible object.
    /// </summary>
    private static Gen<List<ObjectResult>> GenMultiDbWithSkipOnly()
    {
        var genSkipOnlyCount = Gen.Choose(1, 10);
        var genConvertibleCount = Gen.Choose(1, 20);

        return from skipCount in genSkipOnlyCount
               from skipTypes in Gen.ListOf(skipCount, Gen.Elements(ValidObjectTypes))
               from convertibleCount in genConvertibleCount
               from convertibleStatuses in Gen.ListOf(convertibleCount, Gen.Elements(ObjectStatus.Pass, ObjectStatus.FailSyntax, ObjectStatus.FailConvert, ObjectStatus.Skip))
               from convertibleTypes in Gen.ListOf(convertibleCount, Gen.Elements(ValidObjectTypes))
               from forcedStatus in Gen.Elements(ObjectStatus.Pass, ObjectStatus.FailSyntax, ObjectStatus.FailConvert)
               from forcedType in Gen.Elements(ValidObjectTypes)
               let skipObjects = skipTypes.Select((t, i) =>
                   new ObjectResult($"SkipOnlyDB.obj_{i}", t, "SkipOnlyDB", ObjectStatus.Skip)).ToList()
               let convertibleObjects = convertibleStatuses.Zip(convertibleTypes, (s, t) =>
                   new ObjectResult($"ConvertibleDB.obj", t, "ConvertibleDB", s))
                   .Select((r, i) => r with { ObjectName = $"ConvertibleDB.obj_{i}" })
                   .Concat(new[] { new ObjectResult("ConvertibleDB.forced", forcedType, "ConvertibleDB", forcedStatus) })
                   .ToList()
               select skipObjects.Concat(convertibleObjects).ToList();
    }

    #endregion

    /// <summary>
    /// **Validates: Requirements 3.1**
    /// 
    /// For any set of object results in a single database with at least one convertible object,
    /// the per-database Compatibility_Score equals (pass) / (pass + fail-syntax + fail-convert) * 100
    /// rounded to 1 decimal place.
    /// </summary>
    [Property(MaxTest = 200)]
    public Property PerDatabase_Score_Equals_Formula()
    {
        return Prop.ForAll(GenObjectResultsWithConvertible().ToArbitrary(), (List<ObjectResult> objectResults) =>
        {
            var perDbScores = ScoringEngine.ComputePerDatabaseScores(objectResults);

            foreach (var (dbName, dbScore) in perDbScores)
            {
                var dbObjects = objectResults.Where(o => o.DatabaseName == dbName).ToList();
                int pass = dbObjects.Count(o => o.Status == ObjectStatus.Pass);
                int failSyntax = dbObjects.Count(o => o.Status == ObjectStatus.FailSyntax);
                int failConvert = dbObjects.Count(o => o.Status == ObjectStatus.FailConvert);
                int convertible = pass + failSyntax + failConvert;

                if (convertible == 0)
                {
                    dbScore.CompatibilityScore.Should().BeNull(
                        "databases with zero convertible objects should report N/A (null)");
                }
                else
                {
                    double expected = Math.Round((double)pass / convertible * 100, 1);
                    dbScore.CompatibilityScore.Should().Be(expected,
                        $"score for {dbName} should follow the formula: ({pass}/{convertible})*100 rounded to 1dp");
                }
            }
        });
    }

    /// <summary>
    /// **Validates: Requirements 3.3**
    /// 
    /// The aggregate Compatibility_Score is computed as (total pass across all databases) /
    /// (total pass + fail-syntax + fail-convert across all databases) * 100, rounded to 1 decimal,
    /// excluding databases where all objects are "skip".
    /// </summary>
    [Property(MaxTest = 200)]
    public Property Aggregate_Score_Excludes_NA_Databases_And_Follows_Formula()
    {
        return Prop.ForAll(GenObjectResultsWithConvertible().ToArbitrary(), (List<ObjectResult> objectResults) =>
        {
            var perDbScores = ScoringEngine.ComputePerDatabaseScores(objectResults);
            var aggregate = ScoringEngine.ComputeAggregateScore(objectResults);

            // Compute expected aggregate manually - only include databases with convertible objects
            int totalPass = 0, totalFailSyntax = 0, totalFailConvert = 0;
            foreach (var (_, dbScore) in perDbScores)
            {
                if (dbScore.CompatibilityScore is not null)
                {
                    totalPass += dbScore.Pass;
                    totalFailSyntax += dbScore.FailSyntax;
                    totalFailConvert += dbScore.FailConvert;
                }
            }

            int totalConvertible = totalPass + totalFailSyntax + totalFailConvert;
            if (totalConvertible == 0)
            {
                aggregate.CompatibilityScore.Should().BeNull(
                    "aggregate should be N/A when no databases have convertible objects");
            }
            else
            {
                double expected = Math.Round((double)totalPass / totalConvertible * 100, 1);
                aggregate.CompatibilityScore.Should().Be(expected,
                    $"aggregate score should follow the formula: ({totalPass}/{totalConvertible})*100 rounded to 1dp");
            }

            // Verify counts match
            aggregate.TotalPass.Should().Be(totalPass);
            aggregate.TotalFailSyntax.Should().Be(totalFailSyntax);
            aggregate.TotalFailConvert.Should().Be(totalFailConvert);
        });
    }

    /// <summary>
    /// **Validates: Requirements 3.6**
    /// 
    /// Databases where ALL objects are classified as "skip" should have a null (N/A) score
    /// and should be excluded from the aggregate score computation.
    /// </summary>
    [Property(MaxTest = 200)]
    public Property SkipOnly_Databases_Are_NA_And_Excluded_From_Aggregate()
    {
        return Prop.ForAll(GenMultiDbWithSkipOnly().ToArbitrary(), (List<ObjectResult> objectResults) =>
        {
            var perDbScores = ScoringEngine.ComputePerDatabaseScores(objectResults);
            var aggregate = ScoringEngine.ComputeAggregateScore(objectResults);

            // Find skip-only databases
            var skipOnlyDbs = objectResults
                .GroupBy(o => o.DatabaseName)
                .Where(g => g.All(o => o.Status == ObjectStatus.Skip))
                .Select(g => g.Key)
                .ToList();

            // Verify skip-only databases have null score
            foreach (var dbName in skipOnlyDbs)
            {
                perDbScores[dbName].CompatibilityScore.Should().BeNull(
                    $"database '{dbName}' with only skip objects should report N/A");
            }

            // Verify aggregate does not include counts from skip-only databases
            int expectedPassFromNonSkipDbs = objectResults
                .Where(o => !skipOnlyDbs.Contains(o.DatabaseName) && o.Status == ObjectStatus.Pass)
                .Count();

            aggregate.TotalPass.Should().Be(expectedPassFromNonSkipDbs,
                "aggregate TotalPass should only count from databases with at least one convertible object");
        });
    }

    /// <summary>
    /// **Validates: Requirements 3.1, 3.3**
    /// 
    /// The compatibility score is always in the range [0.0, 100.0] when computed,
    /// and is null (N/A) only when there are zero convertible objects.
    /// </summary>
    [Property(MaxTest = 200)]
    public Property Score_Is_Always_Between_0_And_100_Or_NA()
    {
        return Prop.ForAll(GenObjectResultsWithConvertible().ToArbitrary(), (List<ObjectResult> objectResults) =>
        {
            var perDbScores = ScoringEngine.ComputePerDatabaseScores(objectResults);
            var aggregate = ScoringEngine.ComputeAggregateScore(objectResults);

            foreach (var (_, dbScore) in perDbScores)
            {
                if (dbScore.CompatibilityScore is not null)
                {
                    dbScore.CompatibilityScore.Value.Should().BeGreaterThanOrEqualTo(0.0);
                    dbScore.CompatibilityScore.Value.Should().BeLessThanOrEqualTo(100.0);
                }
            }

            if (aggregate.CompatibilityScore is not null)
            {
                aggregate.CompatibilityScore.Value.Should().BeGreaterThanOrEqualTo(0.0);
                aggregate.CompatibilityScore.Value.Should().BeLessThanOrEqualTo(100.0);
            }
        });
    }
}
