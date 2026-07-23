using FluentAssertions;
using FsCheck;
using FsCheck.Xunit;

namespace MigrationAssessment.Pipeline.PropertyTests;

/// <summary>
/// Feature: migration-validation-pipeline, Property 14: Batch Summary Table Completeness
///
/// Validates: Requirements 5.3
///
/// Property 14: For any completed batch execution, the Pipeline Runner SHALL print a summary
/// containing database name, object count, pass count, fail count, and Compatibility_Score
/// for each database that was processed.
/// </summary>
[Trait("Feature", "migration-validation-pipeline")]
[Trait("Property", "14: Batch Summary Table Completeness")]
public class BatchSummaryTableCompletenessPropertyTests
{
    #region Data Models

    /// <summary>
    /// Represents the outcome of processing a single database in a batch execution.
    /// A database either completes successfully (with object results) or fails entirely
    /// (e.g., connection failure).
    /// </summary>
    public record BatchDatabaseResult(
        string DatabaseName,
        bool CompletedSuccessfully,
        List<BatchObjectResult> ObjectResults
    );

    /// <summary>
    /// Represents a single object's validation result within a successfully processed database.
    /// </summary>
    public record BatchObjectResult(
        string ObjectName,
        string ObjectType,
        ObjectStatus Status
    );

    /// <summary>
    /// Represents a single row in the batch summary table output.
    /// The Compatibility_Score is either a numeric string (e.g., "72.2") or "ERROR"
    /// for databases that failed completely.
    /// </summary>
    public record BatchSummaryEntry(
        string DatabaseName,
        int ObjectCount,
        int PassCount,
        int FailCount,
        string CompatibilityScore
    );

    #endregion

    #region Batch Summary Generation Logic (mirrors batch summary in Run-MigrationPipeline.ps1)

    /// <summary>
    /// Generates the batch summary table from completed batch results.
    /// This mirrors the summary table generation logic in the Batch Orchestrator
    /// section of Run-MigrationPipeline.ps1.
    ///
    /// Rules:
    ///   - Every processed database gets an entry in the summary table.
    ///   - Successfully completed databases show: name, object count, pass, fail, score.
    ///   - Object count = pass + fail + skip (all objects processed).
    ///   - Fail count = fail-syntax + fail-convert.
    ///   - Compatibility_Score = pass / (pass + fail) * 100 rounded to 1dp.
    ///   - If pass + fail == 0 (all skip), score is "N/A".
    ///   - Databases that failed completely show "ERROR" for their score.
    /// </summary>
    public static List<BatchSummaryEntry> GenerateBatchSummary(List<BatchDatabaseResult> batchResults)
    {
        var summaryEntries = new List<BatchSummaryEntry>();

        foreach (var dbResult in batchResults)
        {
            if (!dbResult.CompletedSuccessfully)
            {
                // Failed databases show ERROR for score, with zero counts
                summaryEntries.Add(new BatchSummaryEntry(
                    DatabaseName: dbResult.DatabaseName,
                    ObjectCount: 0,
                    PassCount: 0,
                    FailCount: 0,
                    CompatibilityScore: "ERROR"
                ));
            }
            else
            {
                int passCount = dbResult.ObjectResults.Count(o => o.Status == ObjectStatus.Pass);
                int failSyntaxCount = dbResult.ObjectResults.Count(o => o.Status == ObjectStatus.FailSyntax);
                int failConvertCount = dbResult.ObjectResults.Count(o => o.Status == ObjectStatus.FailConvert);
                int skipCount = dbResult.ObjectResults.Count(o => o.Status == ObjectStatus.Skip);

                int objectCount = passCount + failSyntaxCount + failConvertCount + skipCount;
                int failCount = failSyntaxCount + failConvertCount;
                int convertible = passCount + failCount;

                string score;
                if (convertible == 0)
                {
                    score = "N/A";
                }
                else
                {
                    score = Math.Round((double)passCount / convertible * 100, 1).ToString("F1");
                }

                summaryEntries.Add(new BatchSummaryEntry(
                    DatabaseName: dbResult.DatabaseName,
                    ObjectCount: objectCount,
                    PassCount: passCount,
                    FailCount: failCount,
                    CompatibilityScore: score
                ));
            }
        }

        return summaryEntries;
    }

    #endregion

    #region Generators

    private static readonly string[] DatabaseNames =
    {
        "AssessmentTestDB", "ProcedureComplexityDB", "ViewsTriggerDB",
        "TypesAndCLRDB", "CrossSchemaAdvancedDB", "HRDatabase",
        "InventoryDB", "ReportingDB", "AuditDB", "FinanceDB"
    };

    private static readonly string[] ValidObjectTypes =
        { "Table", "View", "StoredProcedure", "Function", "Trigger" };

    /// <summary>
    /// Generates a random list of object results for a successfully completed database.
    /// </summary>
    private static Gen<List<BatchObjectResult>> GenObjectResults()
    {
        var genStatus = Gen.Frequency(
            Tuple.Create(5, Gen.Constant(ObjectStatus.Pass)),
            Tuple.Create(2, Gen.Constant(ObjectStatus.FailSyntax)),
            Tuple.Create(2, Gen.Constant(ObjectStatus.FailConvert)),
            Tuple.Create(1, Gen.Constant(ObjectStatus.Skip))
        );

        var genObject = from objType in Gen.Elements(ValidObjectTypes)
                        from status in genStatus
                        from idx in Gen.Choose(1, 9999)
                        select new BatchObjectResult($"dbo.obj_{idx}", objType, status);

        return from count in Gen.Choose(1, 30)
               from objects in Gen.ListOf(count, genObject)
               select objects.Select((o, i) => o with { ObjectName = $"dbo.obj_{i:D4}" }).ToList();
    }

    /// <summary>
    /// Generates a single database result — either successfully completed or completely failed.
    /// </summary>
    private static Gen<BatchDatabaseResult> GenDatabaseResult(string dbName)
    {
        return Gen.Frequency(
            Tuple.Create(7, GenObjectResults().Select(objects =>
                new BatchDatabaseResult(dbName, CompletedSuccessfully: true, ObjectResults: objects))),
            Tuple.Create(3, Gen.Constant(
                new BatchDatabaseResult(dbName, CompletedSuccessfully: false, ObjectResults: new List<BatchObjectResult>())))
        );
    }

    /// <summary>
    /// Generates a full batch of database results (3-7 databases, mix of success and failure).
    /// Ensures unique database names.
    /// </summary>
    private static Gen<List<BatchDatabaseResult>> GenBatchResults()
    {
        return from dbCount in Gen.Choose(3, 7)
               from selectedNames in Gen.Shuffle(DatabaseNames).Select(names => names.Take(dbCount).ToList())
               from results in GenSequence(selectedNames.Select(GenDatabaseResult).ToList())
               select results;
    }

    /// <summary>
    /// Generates a batch where all databases succeed (no ERROR entries).
    /// </summary>
    private static Gen<List<BatchDatabaseResult>> GenAllSuccessfulBatch()
    {
        return from dbCount in Gen.Choose(2, 5)
               from selectedNames in Gen.Shuffle(DatabaseNames).Select(names => names.Take(dbCount).ToList())
               from results in GenSequence(selectedNames.Select(name =>
                   GenObjectResults().Select(objects =>
                       new BatchDatabaseResult(name, CompletedSuccessfully: true, ObjectResults: objects))
               ).ToList())
               select results;
    }

    /// <summary>
    /// Generates a batch where at least one database fails completely.
    /// </summary>
    private static Gen<List<BatchDatabaseResult>> GenBatchWithFailures()
    {
        return from totalCount in Gen.Choose(3, 6)
               from failCount in Gen.Choose(1, Math.Max(1, totalCount - 1))
               from selectedNames in Gen.Shuffle(DatabaseNames).Select(names => names.Take(totalCount).ToList())
               let successNames = selectedNames.Take(totalCount - failCount).ToList()
               let failNames = selectedNames.Skip(totalCount - failCount).ToList()
               from successResults in GenSequence(successNames.Select(name =>
                   GenObjectResults().Select(objects =>
                       new BatchDatabaseResult(name, CompletedSuccessfully: true, ObjectResults: objects))
               ).ToList())
               let failResults = failNames.Select(name =>
                   new BatchDatabaseResult(name, CompletedSuccessfully: false, ObjectResults: new List<BatchObjectResult>())
               ).ToList()
               select successResults.Concat(failResults).ToList();
    }

    /// <summary>
    /// Helper to sequence a list of Gen into a Gen of list.
    /// </summary>
    private static Gen<List<T>> GenSequence<T>(List<Gen<T>> generators)
    {
        return generators.Aggregate(
            Gen.Constant(new List<T>()),
            (accGen, itemGen) => from acc in accGen
                                 from item in itemGen
                                 select new List<T>(acc) { item }
        );
    }

    #endregion

    #region Property 14 Tests

    /// <summary>
    /// Property 14.1: The summary table contains an entry for EVERY database that was processed.
    /// No database is omitted from the summary.
    ///
    /// <b>Validates: Requirements 5.3</b>
    /// </summary>
    [Property(MaxTest = 100)]
    public Property Summary_contains_entry_for_every_processed_database()
    {
        return Prop.ForAll(
            GenBatchResults().ToArbitrary(),
            batchResults =>
            {
                var summary = GenerateBatchSummary(batchResults);

                summary.Count.Should().Be(batchResults.Count,
                    "summary table must contain exactly one entry per processed database");

                var summaryNames = summary.Select(e => e.DatabaseName).ToHashSet();
                foreach (var dbResult in batchResults)
                {
                    summaryNames.Should().Contain(dbResult.DatabaseName,
                        $"database '{dbResult.DatabaseName}' must appear in the summary table");
                }

                return true.ToProperty();
            });
    }

    /// <summary>
    /// Property 14.2: Each summary entry contains all required fields — database name,
    /// object count, pass count, fail count, and Compatibility_Score.
    /// None of these fields are null or missing.
    ///
    /// <b>Validates: Requirements 5.3</b>
    /// </summary>
    [Property(MaxTest = 100)]
    public Property Each_summary_entry_contains_all_required_fields()
    {
        return Prop.ForAll(
            GenBatchResults().ToArbitrary(),
            batchResults =>
            {
                var summary = GenerateBatchSummary(batchResults);

                foreach (var entry in summary)
                {
                    entry.DatabaseName.Should().NotBeNullOrWhiteSpace(
                        "database name must not be null or empty");

                    entry.ObjectCount.Should().BeGreaterThanOrEqualTo(0,
                        $"object count for '{entry.DatabaseName}' must be non-negative");

                    entry.PassCount.Should().BeGreaterThanOrEqualTo(0,
                        $"pass count for '{entry.DatabaseName}' must be non-negative");

                    entry.FailCount.Should().BeGreaterThanOrEqualTo(0,
                        $"fail count for '{entry.DatabaseName}' must be non-negative");

                    entry.CompatibilityScore.Should().NotBeNullOrWhiteSpace(
                        $"compatibility score for '{entry.DatabaseName}' must not be null or empty");
                }

                return true.ToProperty();
            });
    }

    /// <summary>
    /// Property 14.3: For successfully completed databases, the object count equals the sum
    /// of pass + fail + skip counts (i.e., all objects are accounted for).
    ///
    /// <b>Validates: Requirements 5.3</b>
    /// </summary>
    [Property(MaxTest = 100)]
    public Property Object_count_equals_sum_of_pass_fail_skip()
    {
        return Prop.ForAll(
            GenAllSuccessfulBatch().ToArbitrary(),
            batchResults =>
            {
                var summary = GenerateBatchSummary(batchResults);

                for (int i = 0; i < batchResults.Count; i++)
                {
                    var dbResult = batchResults[i];
                    var entry = summary[i];

                    int skipCount = dbResult.ObjectResults.Count(o => o.Status == ObjectStatus.Skip);
                    int expectedObjectCount = entry.PassCount + entry.FailCount + skipCount;

                    entry.ObjectCount.Should().Be(expectedObjectCount,
                        $"object count for '{entry.DatabaseName}' must equal pass ({entry.PassCount}) + fail ({entry.FailCount}) + skip ({skipCount})");
                }

                return true.ToProperty();
            });
    }

    /// <summary>
    /// Property 14.4: The Compatibility_Score is correctly computed as
    /// pass / (pass + fail) * 100 rounded to 1 decimal place.
    /// When pass + fail == 0 (all skip), score is "N/A".
    ///
    /// <b>Validates: Requirements 5.3</b>
    /// </summary>
    [Property(MaxTest = 100)]
    public Property Compatibility_score_is_correctly_computed()
    {
        return Prop.ForAll(
            GenAllSuccessfulBatch().ToArbitrary(),
            batchResults =>
            {
                var summary = GenerateBatchSummary(batchResults);

                foreach (var entry in summary)
                {
                    int convertible = entry.PassCount + entry.FailCount;

                    if (convertible == 0)
                    {
                        entry.CompatibilityScore.Should().Be("N/A",
                            $"database '{entry.DatabaseName}' with zero convertible objects must show 'N/A'");
                    }
                    else
                    {
                        double expectedScore = Math.Round((double)entry.PassCount / convertible * 100, 1);
                        string expectedScoreStr = expectedScore.ToString("F1");

                        entry.CompatibilityScore.Should().Be(expectedScoreStr,
                            $"database '{entry.DatabaseName}' score should be {expectedScoreStr} " +
                            $"(pass={entry.PassCount}, fail={entry.FailCount})");
                    }
                }

                return true.ToProperty();
            });
    }

    /// <summary>
    /// Property 14.5: Databases that failed completely show "ERROR" for their Compatibility_Score.
    ///
    /// <b>Validates: Requirements 5.3</b>
    /// </summary>
    [Property(MaxTest = 100)]
    public Property Failed_databases_show_error_for_score()
    {
        return Prop.ForAll(
            GenBatchWithFailures().ToArbitrary(),
            batchResults =>
            {
                var summary = GenerateBatchSummary(batchResults);

                var summaryByName = summary.ToDictionary(e => e.DatabaseName);

                foreach (var dbResult in batchResults)
                {
                    summaryByName.Should().ContainKey(dbResult.DatabaseName);
                    var entry = summaryByName[dbResult.DatabaseName];

                    if (!dbResult.CompletedSuccessfully)
                    {
                        entry.CompatibilityScore.Should().Be("ERROR",
                            $"database '{dbResult.DatabaseName}' that failed completely must show 'ERROR'");
                    }
                    else
                    {
                        entry.CompatibilityScore.Should().NotBe("ERROR",
                            $"database '{dbResult.DatabaseName}' that completed successfully must NOT show 'ERROR'");
                    }
                }

                return true.ToProperty();
            });
    }

    /// <summary>
    /// Property 14.6: The pass count and fail count for each entry accurately reflect the
    /// object results. Pass count = objects with status Pass. Fail count = objects with
    /// status FailSyntax + FailConvert.
    ///
    /// <b>Validates: Requirements 5.3</b>
    /// </summary>
    [Property(MaxTest = 100)]
    public Property Pass_and_fail_counts_match_object_results()
    {
        return Prop.ForAll(
            GenAllSuccessfulBatch().ToArbitrary(),
            batchResults =>
            {
                var summary = GenerateBatchSummary(batchResults);

                for (int i = 0; i < batchResults.Count; i++)
                {
                    var dbResult = batchResults[i];
                    var entry = summary[i];

                    int expectedPass = dbResult.ObjectResults.Count(o => o.Status == ObjectStatus.Pass);
                    int expectedFail = dbResult.ObjectResults.Count(o =>
                        o.Status == ObjectStatus.FailSyntax || o.Status == ObjectStatus.FailConvert);

                    entry.PassCount.Should().Be(expectedPass,
                        $"pass count for '{entry.DatabaseName}' must match actual pass objects");

                    entry.FailCount.Should().Be(expectedFail,
                        $"fail count for '{entry.DatabaseName}' must match actual fail-syntax + fail-convert objects");
                }

                return true.ToProperty();
            });
    }

    #endregion

    #region Edge Case Tests

    /// <summary>
    /// Edge case: Batch where all databases fail — every entry shows "ERROR".
    /// </summary>
    [Fact]
    public void All_databases_failed_shows_error_for_all()
    {
        var batchResults = new List<BatchDatabaseResult>
        {
            new("DB1", CompletedSuccessfully: false, ObjectResults: new List<BatchObjectResult>()),
            new("DB2", CompletedSuccessfully: false, ObjectResults: new List<BatchObjectResult>()),
            new("DB3", CompletedSuccessfully: false, ObjectResults: new List<BatchObjectResult>()),
        };

        var summary = GenerateBatchSummary(batchResults);

        summary.Should().HaveCount(3);
        summary.Should().AllSatisfy(entry =>
        {
            entry.CompatibilityScore.Should().Be("ERROR");
            entry.ObjectCount.Should().Be(0);
            entry.PassCount.Should().Be(0);
            entry.FailCount.Should().Be(0);
        });
    }

    /// <summary>
    /// Edge case: Database with all objects as "skip" shows score "N/A".
    /// </summary>
    [Fact]
    public void All_skip_objects_shows_na_score()
    {
        var batchResults = new List<BatchDatabaseResult>
        {
            new("SkipDB", CompletedSuccessfully: true, ObjectResults: new List<BatchObjectResult>
            {
                new("dbo.syn_A", "Synonym", ObjectStatus.Skip),
                new("dbo.syn_B", "Synonym", ObjectStatus.Skip),
                new("dbo.seq_C", "Sequence", ObjectStatus.Skip),
            }),
        };

        var summary = GenerateBatchSummary(batchResults);

        summary.Should().HaveCount(1);
        var entry = summary[0];
        entry.DatabaseName.Should().Be("SkipDB");
        entry.ObjectCount.Should().Be(3);
        entry.PassCount.Should().Be(0);
        entry.FailCount.Should().Be(0);
        entry.CompatibilityScore.Should().Be("N/A");
    }

    /// <summary>
    /// Edge case: Database with perfect score (all pass) shows "100.0".
    /// </summary>
    [Fact]
    public void All_pass_objects_shows_100_score()
    {
        var batchResults = new List<BatchDatabaseResult>
        {
            new("PerfectDB", CompletedSuccessfully: true, ObjectResults: new List<BatchObjectResult>
            {
                new("dbo.tbl_A", "Table", ObjectStatus.Pass),
                new("dbo.tbl_B", "Table", ObjectStatus.Pass),
                new("dbo.vw_C", "View", ObjectStatus.Pass),
                new("dbo.sp_D", "StoredProcedure", ObjectStatus.Pass),
            }),
        };

        var summary = GenerateBatchSummary(batchResults);

        summary.Should().HaveCount(1);
        var entry = summary[0];
        entry.DatabaseName.Should().Be("PerfectDB");
        entry.ObjectCount.Should().Be(4);
        entry.PassCount.Should().Be(4);
        entry.FailCount.Should().Be(0);
        entry.CompatibilityScore.Should().Be("100.0");
    }

    /// <summary>
    /// Edge case: Specific known computation — 7 pass, 3 fail → 70.0%.
    /// </summary>
    [Fact]
    public void Known_computation_7_pass_3_fail_gives_70_percent()
    {
        var objects = Enumerable.Range(0, 7)
            .Select(i => new BatchObjectResult($"dbo.pass_{i}", "Table", ObjectStatus.Pass))
            .Concat(Enumerable.Range(0, 2)
                .Select(i => new BatchObjectResult($"dbo.failsyn_{i}", "StoredProcedure", ObjectStatus.FailSyntax)))
            .Concat(new[] { new BatchObjectResult("dbo.failconv_0", "Function", ObjectStatus.FailConvert) })
            .ToList();

        var batchResults = new List<BatchDatabaseResult>
        {
            new("TestDB", CompletedSuccessfully: true, ObjectResults: objects),
        };

        var summary = GenerateBatchSummary(batchResults);

        var entry = summary[0];
        entry.ObjectCount.Should().Be(10);
        entry.PassCount.Should().Be(7);
        entry.FailCount.Should().Be(3);
        entry.CompatibilityScore.Should().Be("70.0");
    }

    /// <summary>
    /// Edge case: Mixed batch with one success and one failure.
    /// </summary>
    [Fact]
    public void Mixed_batch_success_and_failure()
    {
        var batchResults = new List<BatchDatabaseResult>
        {
            new("SuccessDB", CompletedSuccessfully: true, ObjectResults: new List<BatchObjectResult>
            {
                new("dbo.tbl_A", "Table", ObjectStatus.Pass),
                new("dbo.sp_B", "StoredProcedure", ObjectStatus.FailSyntax),
            }),
            new("FailedDB", CompletedSuccessfully: false, ObjectResults: new List<BatchObjectResult>()),
        };

        var summary = GenerateBatchSummary(batchResults);

        summary.Should().HaveCount(2);

        var successEntry = summary.First(e => e.DatabaseName == "SuccessDB");
        successEntry.ObjectCount.Should().Be(2);
        successEntry.PassCount.Should().Be(1);
        successEntry.FailCount.Should().Be(1);
        successEntry.CompatibilityScore.Should().Be("50.0");

        var failedEntry = summary.First(e => e.DatabaseName == "FailedDB");
        failedEntry.ObjectCount.Should().Be(0);
        failedEntry.PassCount.Should().Be(0);
        failedEntry.FailCount.Should().Be(0);
        failedEntry.CompatibilityScore.Should().Be("ERROR");
    }

    #endregion
}
