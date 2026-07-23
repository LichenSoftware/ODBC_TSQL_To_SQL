using FluentAssertions;
using FsCheck;
using FsCheck.Xunit;

namespace MigrationAssessment.Pipeline.PropertyTests;

/// <summary>
/// Property-based tests for Score Progression Delta computation in the Scoring Engine.
/// Property 11: Score Progression Delta — For any pipeline run that has a previous run
/// for the same database, the Scoring Report SHALL include the previous Compatibility_Score
/// and a delta value equal to (current score − previous score) for each database.
///
/// **Validates: Requirements 4.5**
/// </summary>
[Trait("Feature", "migration-validation-pipeline")]
[Trait("Property", "11: Score Progression Delta")]
public class ScoreProgressionDeltaPropertyTests
{
    private static readonly string[] ValidStatuses = { "pass", "fail-syntax", "fail-convert", "skip" };
    private static readonly string[] ValidObjectTypes = { "Table", "View", "StoredProcedure", "Function", "Trigger" };

    #region Generators

    /// <summary>
    /// Generates a random compatibility score between 0.0 and 100.0 (rounded to 1 decimal).
    /// </summary>
    private static Gen<double> GenScore()
    {
        return from raw in Gen.Choose(0, 1000)
               select Math.Round(raw / 10.0, 1);
    }

    /// <summary>
    /// Generates a set of object results for a single database that produces a numeric score
    /// (i.e., at least one non-skip object).
    /// </summary>
    private static Gen<List<ObjectResult>> GenObjectResultsWithNumericScore(string databaseName)
    {
        // Ensure at least one non-skip object to get a numeric score
        var genNonSkipStatus = Gen.Elements("pass", "fail-syntax", "fail-convert");
        var genObjectType = Gen.Elements(ValidObjectTypes);

        return from count in Gen.Choose(1, 20)
               from statuses in Gen.ListOf(count, genNonSkipStatus)
               from types in Gen.ListOf(count, genObjectType)
               select statuses.Zip(types, (status, type) => new ObjectResult
               {
                   ObjectName = $"dbo.Obj_{Guid.NewGuid():N}",
                   ObjectType = type,
                   DatabaseName = databaseName,
                   Status = status
               }).ToList();
    }

    /// <summary>
    /// Generates a set of object results that are ALL skip (produces N/A score).
    /// </summary>
    private static Gen<List<ObjectResult>> GenAllSkipObjectResults(string databaseName)
    {
        return from count in Gen.Choose(1, 5)
               from types in Gen.ListOf(count, Gen.Elements(ValidObjectTypes))
               select types.Select(type => new ObjectResult
               {
                   ObjectName = $"dbo.Skip_{Guid.NewGuid():N}",
                   ObjectType = type,
                   DatabaseName = databaseName,
                   Status = "skip"
               }).ToList();
    }

    #endregion

    /// <summary>
    /// Property 11.1: When both current score and previous score are numeric,
    /// delta = current score − previous score (rounded to 1 decimal place).
    ///
    /// **Validates: Requirements 4.5**
    /// </summary>
    [Property(MaxTest = 100)]
    public Property Delta_Equals_CurrentMinusPrevious_WhenBothNumeric()
    {
        var gen = from dbName in Gen.Elements("TestDB_A", "TestDB_B", "TestDB_C")
                  from objectResults in GenObjectResultsWithNumericScore(dbName)
                  from previousScore in GenScore()
                  select (dbName, objectResults, previousScore);

        return Prop.ForAll(gen.ToArbitrary(), tuple =>
        {
            var (dbName, objectResults, previousScore) = tuple;

            // Compute expected current score
            int passCount = objectResults.Count(o => o.Status == "pass");
            int failSyntaxCount = objectResults.Count(o => o.Status == "fail-syntax");
            int failConvertCount = objectResults.Count(o => o.Status == "fail-convert");
            int convertible = passCount + failSyntaxCount + failConvertCount;

            // Should have at least 1 convertible object
            if (convertible == 0) return;

            double expectedCurrentScore = Math.Round((double)passCount / convertible * 100, 1);
            double expectedDelta = Math.Round(expectedCurrentScore - previousScore, 1);

            // Run the scoring engine
            var previousScores = new Dictionary<string, object?>
            {
                [dbName] = previousScore
            };

            var result = ScoringEngine.ComputeScores(objectResults, previousScores);

            // Verify delta
            var dbResult = result.Databases[dbName];
            dbResult.PreviousScore.Should().Be(previousScore,
                "the previous score should be included in the report");
            dbResult.Delta.Should().Be(expectedDelta,
                $"delta should equal current ({expectedCurrentScore}) − previous ({previousScore}) = {expectedDelta}");
        });
    }

    /// <summary>
    /// Property 11.2: When the previous score is null (no previous run exists),
    /// the delta should be null.
    ///
    /// **Validates: Requirements 4.5**
    /// </summary>
    [Property(MaxTest = 100)]
    public Property Delta_IsNull_WhenPreviousScoreIsNull()
    {
        var gen = from dbName in Gen.Elements("TestDB_X", "TestDB_Y", "TestDB_Z")
                  from objectResults in GenObjectResultsWithNumericScore(dbName)
                  select (dbName, objectResults);

        return Prop.ForAll(gen.ToArbitrary(), tuple =>
        {
            var (dbName, objectResults) = tuple;

            // No previous scores at all
            var previousScores = new Dictionary<string, object?>();

            var result = ScoringEngine.ComputeScores(objectResults, previousScores);

            var dbResult = result.Databases[dbName];
            dbResult.PreviousScore.Should().BeNull(
                "when no previous run exists, previous score should be null");
            dbResult.Delta.Should().BeNull(
                "when no previous run exists, delta should be null");
        });
    }

    /// <summary>
    /// Property 11.3: When the current score is N/A (all objects are skip),
    /// the delta should be null regardless of previous score.
    ///
    /// **Validates: Requirements 4.5**
    /// </summary>
    [Property(MaxTest = 100)]
    public Property Delta_IsNull_WhenCurrentScoreIsNA()
    {
        var gen = from dbName in Gen.Elements("TestDB_NA1", "TestDB_NA2")
                  from objectResults in GenAllSkipObjectResults(dbName)
                  from previousScore in GenScore()
                  select (dbName, objectResults, previousScore);

        return Prop.ForAll(gen.ToArbitrary(), tuple =>
        {
            var (dbName, objectResults, previousScore) = tuple;

            var previousScores = new Dictionary<string, object?>
            {
                [dbName] = previousScore
            };

            var result = ScoringEngine.ComputeScores(objectResults, previousScores);

            var dbResult = result.Databases[dbName];
            dbResult.Delta.Should().BeNull(
                "when current score is N/A (all skip), delta should be null");
        });
    }

    /// <summary>
    /// Property 11.4: When the previous score is explicitly N/A (stored as string "N/A"),
    /// the delta should be null even when current score is numeric.
    ///
    /// **Validates: Requirements 4.5**
    /// </summary>
    [Property(MaxTest = 100)]
    public Property Delta_IsNull_WhenPreviousScoreIsNA()
    {
        var gen = from dbName in Gen.Elements("TestDB_P1", "TestDB_P2")
                  from objectResults in GenObjectResultsWithNumericScore(dbName)
                  select (dbName, objectResults);

        return Prop.ForAll(gen.ToArbitrary(), tuple =>
        {
            var (dbName, objectResults) = tuple;

            // Previous score is "N/A" (string)
            var previousScores = new Dictionary<string, object?>
            {
                [dbName] = "N/A"
            };

            var result = ScoringEngine.ComputeScores(objectResults, previousScores);

            var dbResult = result.Databases[dbName];
            dbResult.PreviousScore.Should().Be("N/A",
                "previous score N/A should be preserved in the report");
            dbResult.Delta.Should().BeNull(
                "when previous score is N/A, delta should be null");
        });
    }

    #region Supporting Types

    /// <summary>
    /// Represents a single object result from the pipeline.
    /// </summary>
    public class ObjectResult
    {
        public required string ObjectName { get; set; }
        public required string ObjectType { get; set; }
        public required string DatabaseName { get; set; }
        public required string Status { get; set; }
    }

    /// <summary>
    /// Result of scoring computation for a single database.
    /// </summary>
    public class DatabaseScoreResult
    {
        public object? CompatibilityScore { get; set; }
        public object? PreviousScore { get; set; }
        public double? Delta { get; set; }
        public int Pass { get; set; }
        public int FailSyntax { get; set; }
        public int FailConvert { get; set; }
        public int Skip { get; set; }
    }

    /// <summary>
    /// Aggregate scoring result.
    /// </summary>
    public class ScoringResult
    {
        public Dictionary<string, DatabaseScoreResult> Databases { get; set; } = new();
    }

    /// <summary>
    /// C# implementation of the Scoring Engine's delta computation logic,
    /// mirroring the PowerShell Invoke-Scoring.ps1 behavior.
    /// This allows property-based testing of the core scoring algorithm.
    /// </summary>
    public static class ScoringEngine
    {
        public static ScoringResult ComputeScores(
            List<ObjectResult> objectResults,
            Dictionary<string, object?> previousScores)
        {
            var result = new ScoringResult();

            // Group by database
            var byDatabase = objectResults.GroupBy(o => o.DatabaseName);

            foreach (var dbGroup in byDatabase)
            {
                var dbName = dbGroup.Key;
                var objects = dbGroup.ToList();

                int pass = objects.Count(o => o.Status == "pass");
                int failSyntax = objects.Count(o => o.Status == "fail-syntax");
                int failConvert = objects.Count(o => o.Status == "fail-convert");
                int skip = objects.Count(o => o.Status == "skip");

                int convertible = pass + failSyntax + failConvert;

                object? compatibilityScore;
                if (convertible == 0)
                {
                    compatibilityScore = "N/A";
                }
                else
                {
                    compatibilityScore = Math.Round((double)pass / convertible * 100, 1);
                }

                // Compute delta
                object? previousScore = null;
                double? delta = null;

                if (previousScores.ContainsKey(dbName))
                {
                    previousScore = previousScores[dbName];

                    if (compatibilityScore is double currentNumeric
                        && previousScore != null
                        && previousScore is not string) // "N/A" is a string
                    {
                        double prevNumeric = Convert.ToDouble(previousScore);
                        delta = Math.Round(currentNumeric - prevNumeric, 1);
                    }
                }

                result.Databases[dbName] = new DatabaseScoreResult
                {
                    CompatibilityScore = compatibilityScore,
                    PreviousScore = previousScore,
                    Delta = delta,
                    Pass = pass,
                    FailSyntax = failSyntax,
                    FailConvert = failConvert,
                    Skip = skip
                };
            }

            return result;
        }
    }

    #endregion
}
