using FluentAssertions;
using FsCheck;
using FsCheck.Xunit;

namespace MigrationAssessment.Pipeline.PropertyTests;

/// <summary>
/// Feature: migration-validation-pipeline, Property 13: Batch Resilience on Database Failure
///
/// Validates: Requirements 5.4
///
/// Property 13: For any batch execution where one or more databases fail completely
/// (e.g., connection failure), the Pipeline Runner SHALL log the error for the failed
/// database(s) and continue executing the pipeline for all remaining configured databases.
/// </summary>
[Trait("Feature", "migration-validation-pipeline")]
[Trait("Property", "13: Batch Resilience on Database Failure")]
public class BatchResiliencePropertyTests
{
    #region Data Models

    /// <summary>
    /// Represents a database entry in the batch configuration (pipeline-config.json).
    /// </summary>
    public record BatchDatabaseConfig(
        string Name,
        string ConnectionString,
        string SessionName,
        string SetupScriptPath
    );

    /// <summary>
    /// Represents the outcome of running the pipeline for a single database.
    /// </summary>
    public record DatabasePipelineResult(
        string DatabaseName,
        bool Success,
        string? ErrorMessage,
        int ObjectCount,
        int PassCount,
        int FailCount,
        double? CompatibilityScore
    );

    /// <summary>
    /// Represents a log entry recorded for a failed database during batch execution.
    /// </summary>
    public record FailureLogEntry(
        string DatabaseName,
        string ErrorMessage,
        DateTime Timestamp
    );

    /// <summary>
    /// The combined batch execution result containing results from all databases
    /// (successful and failed) plus failure logs.
    /// </summary>
    public record BatchExecutionResult(
        List<DatabasePipelineResult> DatabaseResults,
        List<FailureLogEntry> FailureLogs
    );

    #endregion

    #region Batch Orchestrator Logic (C# model of Run-MigrationPipeline.ps1 batch mode)

    /// <summary>
    /// Simulates the Batch Orchestrator behavior from Run-MigrationPipeline.ps1.
    /// 
    /// Core resilience behavior:
    /// - Iterates over each configured database sequentially
    /// - If a database fails (connection error, pipeline error), logs the failure and continues
    /// - A single database failure does NOT halt the entire batch
    /// - Failed databases are recorded in the combined report with status "pipeline-error"
    /// - All successfully processed databases produce normal results
    /// - The combined result includes entries for ALL databases (failed + successful)
    /// </summary>
    public static BatchExecutionResult ExecuteBatch(
        List<BatchDatabaseConfig> databaseConfigs,
        Func<BatchDatabaseConfig, DatabasePipelineResult> executePipeline)
    {
        var results = new List<DatabasePipelineResult>();
        var failureLogs = new List<FailureLogEntry>();

        foreach (var dbConfig in databaseConfigs)
        {
            try
            {
                var result = executePipeline(dbConfig);

                if (!result.Success)
                {
                    // Database failed — log error and record as failed
                    failureLogs.Add(new FailureLogEntry(
                        dbConfig.Name,
                        result.ErrorMessage ?? "Unknown pipeline error",
                        DateTime.UtcNow
                    ));
                }

                results.Add(result);
            }
            catch (Exception ex)
            {
                // Unhandled exception from pipeline execution — catch and continue
                var failResult = new DatabasePipelineResult(
                    dbConfig.Name,
                    Success: false,
                    ErrorMessage: ex.Message,
                    ObjectCount: 0,
                    PassCount: 0,
                    FailCount: 0,
                    CompatibilityScore: null
                );
                results.Add(failResult);
                failureLogs.Add(new FailureLogEntry(
                    dbConfig.Name,
                    ex.Message,
                    DateTime.UtcNow
                ));
            }
        }

        return new BatchExecutionResult(results, failureLogs);
    }

    #endregion

    #region Generators

    private static readonly string[] DatabaseNames =
    {
        "AssessmentTestDB", "ProcedureComplexityDB", "ViewsTriggerDB",
        "TypesAndCLRDB", "CrossSchemaAdvancedDB", "ExtraTestDB1",
        "ExtraTestDB2", "CustomAppDB", "LegacyDataDB"
    };

    /// <summary>
    /// Generates a batch database configuration entry.
    /// </summary>
    private static Gen<BatchDatabaseConfig> GenDatabaseConfig(string name)
    {
        return from sessionName in Gen.Constant($"session-{name.ToLowerInvariant()}")
               from scriptPath in Gen.Constant($"scripts/setup-{name.ToLowerInvariant()}.sql")
               select new BatchDatabaseConfig(
                   name,
                   $"Server=localhost;Database={name};Trusted_Connection=True;",
                   sessionName,
                   scriptPath
               );
    }

    /// <summary>
    /// Generates a batch configuration with unique database names.
    /// Guarantees at least 2 databases so we can have at least one fail and one succeed.
    /// </summary>
    private static Gen<List<BatchDatabaseConfig>> GenBatchConfig()
    {
        return from count in Gen.Choose(2, 8)
               from selectedNames in Gen.Shuffle(DatabaseNames).Select(arr => arr.Take(count).ToList())
               from configs in GenHelpers.Sequence(selectedNames.Select(GenDatabaseConfig).ToList())
               select configs;
    }

    /// <summary>
    /// Generates the set of database indices that will fail during batch execution.
    /// Guarantees at least 1 failure and at least 1 success.
    /// </summary>
    private static Gen<HashSet<int>> GenFailureIndices(int totalCount)
    {
        // At least 1 fail and at least 1 success
        var maxFailures = totalCount - 1;
        return from failCount in Gen.Choose(1, maxFailures)
               from indices in Gen.Shuffle(Enumerable.Range(0, totalCount).ToArray())
                   .Select(arr => arr.Take(failCount).ToHashSet())
               select indices;
    }

    /// <summary>
    /// Generates a successful pipeline result for a database.
    /// </summary>
    private static Gen<DatabasePipelineResult> GenSuccessResult(string dbName)
    {
        return from objectCount in Gen.Choose(5, 50)
               from passRate in Gen.Choose(30, 100)
               let passCount = (int)Math.Round(objectCount * passRate / 100.0)
               let failCount = objectCount - passCount
               let score = objectCount > 0
                   ? Math.Round((double)passCount / objectCount * 100, 1)
                   : 0.0
               select new DatabasePipelineResult(
                   dbName,
                   Success: true,
                   ErrorMessage: null,
                   ObjectCount: objectCount,
                   PassCount: passCount,
                   FailCount: failCount,
                   CompatibilityScore: score
               );
    }

    /// <summary>
    /// Generates a failed pipeline result for a database (simulating connection errors etc.).
    /// </summary>
    private static Gen<DatabasePipelineResult> GenFailureResult(string dbName)
    {
        return from errorMsg in Gen.Elements(
                   "Connection timeout: Unable to connect to SQL Server instance",
                   "Login failed for user 'pipeline_user'",
                   "Cannot open database. Login failed.",
                   "Network path not found",
                   "TCP Provider: No connection could be made because the target machine actively refused it",
                   "A transport-level error has occurred when receiving results from the server")
               select new DatabasePipelineResult(
                   dbName,
                   Success: false,
                   ErrorMessage: errorMsg,
                   ObjectCount: 0,
                   PassCount: 0,
                   FailCount: 0,
                   CompatibilityScore: null
               );
    }

    /// <summary>
    /// Generates a complete batch scenario: config + which databases fail + expected results.
    /// Returns (configs, failureIndices, successResults, failureResults).
    /// </summary>
    private static Gen<(List<BatchDatabaseConfig> Configs, HashSet<int> FailureIndices,
        Dictionary<string, DatabasePipelineResult> SuccessResults,
        Dictionary<string, DatabasePipelineResult> FailureResults)> GenBatchScenario()
    {
        return GenBatchConfig().SelectMany(configs =>
            GenFailureIndices(configs.Count).SelectMany(failureIndices =>
            {
                var successGens = configs
                    .Select((cfg, idx) => failureIndices.Contains(idx)
                        ? (Gen<(string Name, DatabasePipelineResult Result, bool IsFail)>?)null
                        : GenSuccessResult(cfg.Name).Select(r => (cfg.Name, Result: r, IsFail: false)))
                    .Where(g => g != null)
                    .Cast<Gen<(string Name, DatabasePipelineResult Result, bool IsFail)>>()
                    .ToList();

                var failureGens = configs
                    .Select((cfg, idx) => failureIndices.Contains(idx)
                        ? GenFailureResult(cfg.Name).Select(r => (cfg.Name, Result: r, IsFail: true))
                        : (Gen<(string Name, DatabasePipelineResult Result, bool IsFail)>?)null)
                    .Where(g => g != null)
                    .Cast<Gen<(string Name, DatabasePipelineResult Result, bool IsFail)>>()
                    .ToList();

                var allGens = successGens.Concat(failureGens).ToList();

                return GenHelpers.Sequence(allGens).Select(allResults =>
                {
                    var successDict = allResults
                        .Where(r => !r.IsFail)
                        .ToDictionary(r => r.Name, r => r.Result);
                    var failDict = allResults
                        .Where(r => r.IsFail)
                        .ToDictionary(r => r.Name, r => r.Result);
                    return (configs, failureIndices, successDict, failDict);
                });
            }));
    }

    #endregion

    #region Property 13 Tests

    /// <summary>
    /// Property 13.1: For any batch execution where 1+ databases fail, the remaining
    /// databases still get processed — their results appear in the combined output.
    ///
    /// <b>Validates: Requirements 5.4</b>
    /// </summary>
    [Property(MaxTest = 100)]
    public Property Remaining_databases_are_processed_when_some_fail()
    {
        return Prop.ForAll(
            GenBatchScenario().ToArbitrary(),
            scenario =>
            {
                var (configs, failureIndices, successResults, failureResults) = scenario;

                // Build a lookup: database name → expected result
                var allExpected = new Dictionary<string, DatabasePipelineResult>();
                foreach (var kvp in successResults) allExpected[kvp.Key] = kvp.Value;
                foreach (var kvp in failureResults) allExpected[kvp.Key] = kvp.Value;

                // Execute batch with our simulated pipeline
                var batchResult = ExecuteBatch(configs, cfg => allExpected[cfg.Name]);

                // Verify all successful databases appear in results
                foreach (var kvp in successResults)
                {
                    batchResult.DatabaseResults
                        .Should().Contain(r => r.DatabaseName == kvp.Key && r.Success,
                            $"successful database '{kvp.Key}' must appear in batch results");
                }

                return true.ToProperty();
            });
    }

    /// <summary>
    /// Property 13.2: A failed database does NOT halt the batch — databases configured
    /// after the failed one are still processed.
    ///
    /// <b>Validates: Requirements 5.4</b>
    /// </summary>
    [Property(MaxTest = 100)]
    public Property Failed_database_does_not_halt_batch_execution()
    {
        return Prop.ForAll(
            GenBatchScenario().ToArbitrary(),
            scenario =>
            {
                var (configs, failureIndices, successResults, failureResults) = scenario;

                var allExpected = new Dictionary<string, DatabasePipelineResult>();
                foreach (var kvp in successResults) allExpected[kvp.Key] = kvp.Value;
                foreach (var kvp in failureResults) allExpected[kvp.Key] = kvp.Value;

                // Track execution order
                var executionOrder = new List<string>();
                var batchResult = ExecuteBatch(configs, cfg =>
                {
                    executionOrder.Add(cfg.Name);
                    return allExpected[cfg.Name];
                });

                // ALL configured databases must have been attempted (executed)
                executionOrder.Should().HaveCount(configs.Count,
                    "all databases must be attempted even when some fail");

                // Verify execution order matches config order (sequential processing)
                for (int i = 0; i < configs.Count; i++)
                {
                    executionOrder[i].Should().Be(configs[i].Name,
                        $"database at position {i} must be executed in config order");
                }

                return true.ToProperty();
            });
    }

    /// <summary>
    /// Property 13.3: The combined results include entries from ALL successfully processed
    /// databases — no successful database is silently dropped because another failed.
    ///
    /// <b>Validates: Requirements 5.4</b>
    /// </summary>
    [Property(MaxTest = 100)]
    public Property Combined_results_include_all_successful_database_entries()
    {
        return Prop.ForAll(
            GenBatchScenario().ToArbitrary(),
            scenario =>
            {
                var (configs, failureIndices, successResults, failureResults) = scenario;

                var allExpected = new Dictionary<string, DatabasePipelineResult>();
                foreach (var kvp in successResults) allExpected[kvp.Key] = kvp.Value;
                foreach (var kvp in failureResults) allExpected[kvp.Key] = kvp.Value;

                var batchResult = ExecuteBatch(configs, cfg => allExpected[cfg.Name]);

                // Every successful database must have its correct result in the output
                foreach (var (dbName, expectedResult) in successResults)
                {
                    var actualResult = batchResult.DatabaseResults
                        .FirstOrDefault(r => r.DatabaseName == dbName);

                    actualResult.Should().NotBeNull(
                        $"successful database '{dbName}' must have a result entry");
                    actualResult!.Success.Should().BeTrue(
                        $"database '{dbName}' was expected to succeed");
                    actualResult.ObjectCount.Should().Be(expectedResult.ObjectCount,
                        $"database '{dbName}' object count must match");
                    actualResult.PassCount.Should().Be(expectedResult.PassCount,
                        $"database '{dbName}' pass count must match");
                    actualResult.CompatibilityScore.Should().Be(expectedResult.CompatibilityScore,
                        $"database '{dbName}' score must match");
                }

                return true.ToProperty();
            });
    }

    /// <summary>
    /// Property 13.4: Failed databases are recorded/logged — they are NOT silently dropped.
    /// The failure log contains an entry for each failed database.
    ///
    /// <b>Validates: Requirements 5.4</b>
    /// </summary>
    [Property(MaxTest = 100)]
    public Property Failed_databases_are_logged_not_silently_dropped()
    {
        return Prop.ForAll(
            GenBatchScenario().ToArbitrary(),
            scenario =>
            {
                var (configs, failureIndices, successResults, failureResults) = scenario;

                var allExpected = new Dictionary<string, DatabasePipelineResult>();
                foreach (var kvp in successResults) allExpected[kvp.Key] = kvp.Value;
                foreach (var kvp in failureResults) allExpected[kvp.Key] = kvp.Value;

                var batchResult = ExecuteBatch(configs, cfg => allExpected[cfg.Name]);

                // Every failed database must have a failure log entry
                foreach (var (dbName, failResult) in failureResults)
                {
                    batchResult.FailureLogs.Should().Contain(
                        log => log.DatabaseName == dbName,
                        $"failed database '{dbName}' must have a failure log entry");

                    var logEntry = batchResult.FailureLogs.First(l => l.DatabaseName == dbName);
                    logEntry.ErrorMessage.Should().NotBeNullOrEmpty(
                        $"failure log for '{dbName}' must contain an error message");
                }

                // Failed databases must also appear in the results list (not dropped)
                foreach (var dbName in failureResults.Keys)
                {
                    batchResult.DatabaseResults
                        .Should().Contain(r => r.DatabaseName == dbName,
                            $"failed database '{dbName}' must still have an entry in the combined results");
                }

                return true.ToProperty();
            });
    }

    /// <summary>
    /// Property 13.5: The total number of result entries (failed + successful) equals the
    /// total number of configured databases — nothing is lost.
    ///
    /// <b>Validates: Requirements 5.4</b>
    /// </summary>
    [Property(MaxTest = 100)]
    public Property Total_result_entries_equals_configured_database_count()
    {
        return Prop.ForAll(
            GenBatchScenario().ToArbitrary(),
            scenario =>
            {
                var (configs, failureIndices, successResults, failureResults) = scenario;

                var allExpected = new Dictionary<string, DatabasePipelineResult>();
                foreach (var kvp in successResults) allExpected[kvp.Key] = kvp.Value;
                foreach (var kvp in failureResults) allExpected[kvp.Key] = kvp.Value;

                var batchResult = ExecuteBatch(configs, cfg => allExpected[cfg.Name]);

                batchResult.DatabaseResults.Should().HaveCount(configs.Count,
                    $"total result entries ({batchResult.DatabaseResults.Count}) " +
                    $"must equal configured database count ({configs.Count})");

                return true.ToProperty();
            });
    }

    #endregion

    #region Edge Case: Exception-throwing databases

    /// <summary>
    /// When a database pipeline throws an unhandled exception, the batch orchestrator
    /// catches it, logs the failure, and continues with the next database.
    ///
    /// <b>Validates: Requirements 5.4</b>
    /// </summary>
    [Property(MaxTest = 100)]
    public Property Unhandled_exceptions_are_caught_and_batch_continues()
    {
        return Prop.ForAll(
            GenBatchScenario().ToArbitrary(),
            scenario =>
            {
                var (configs, failureIndices, successResults, failureResults) = scenario;

                var allExpected = new Dictionary<string, DatabasePipelineResult>();
                foreach (var kvp in successResults) allExpected[kvp.Key] = kvp.Value;
                // Failure databases will throw instead of returning a result

                // Execute batch where failed databases throw exceptions
                var batchResult = ExecuteBatch(configs, cfg =>
                {
                    if (failureResults.ContainsKey(cfg.Name))
                    {
                        throw new InvalidOperationException(
                            $"Connection failed for {cfg.Name}: {failureResults[cfg.Name].ErrorMessage}");
                    }
                    return allExpected[cfg.Name];
                });

                // Batch must still complete and have results for ALL databases
                batchResult.DatabaseResults.Should().HaveCount(configs.Count,
                    "batch must have results for all databases even when some throw exceptions");

                // Successful databases must still have correct results
                foreach (var (dbName, expected) in successResults)
                {
                    var actual = batchResult.DatabaseResults.First(r => r.DatabaseName == dbName);
                    actual.Success.Should().BeTrue(
                        $"successful database '{dbName}' must still succeed despite other exceptions");
                }

                // Exception-throwing databases must be logged
                foreach (var dbName in failureResults.Keys)
                {
                    batchResult.FailureLogs.Should().Contain(
                        log => log.DatabaseName == dbName,
                        $"exception-throwing database '{dbName}' must be in failure logs");
                }

                return true.ToProperty();
            });
    }

    #endregion

    #region Edge Case: All databases fail

    /// <summary>
    /// When every database in the batch fails, the batch still completes (does not crash),
    /// all failures are logged, and the result count matches the config count.
    /// </summary>
    [Fact]
    public void Batch_completes_even_when_all_databases_fail()
    {
        var configs = new List<BatchDatabaseConfig>
        {
            new("DB1", "Server=localhost;Database=DB1;", "session-db1", "scripts/setup-db1.sql"),
            new("DB2", "Server=localhost;Database=DB2;", "session-db2", "scripts/setup-db2.sql"),
            new("DB3", "Server=localhost;Database=DB3;", "session-db3", "scripts/setup-db3.sql"),
        };

        var batchResult = ExecuteBatch(configs, cfg =>
            new DatabasePipelineResult(cfg.Name, Success: false,
                "Connection refused", 0, 0, 0, null));

        batchResult.DatabaseResults.Should().HaveCount(3);
        batchResult.DatabaseResults.Should().OnlyContain(r => !r.Success);
        batchResult.FailureLogs.Should().HaveCount(3);
        batchResult.FailureLogs.Select(l => l.DatabaseName)
            .Should().BeEquivalentTo(new[] { "DB1", "DB2", "DB3" });
    }

    /// <summary>
    /// When only the first database fails, all subsequent databases are still processed.
    /// </summary>
    [Fact]
    public void First_database_failure_does_not_prevent_subsequent_processing()
    {
        var configs = new List<BatchDatabaseConfig>
        {
            new("FailingDB", "Server=bad;Database=FailingDB;", "session-failing", "scripts/setup-failing.sql"),
            new("SuccessDB1", "Server=localhost;Database=SuccessDB1;", "session-s1", "scripts/setup-s1.sql"),
            new("SuccessDB2", "Server=localhost;Database=SuccessDB2;", "session-s2", "scripts/setup-s2.sql"),
        };

        var batchResult = ExecuteBatch(configs, cfg =>
        {
            if (cfg.Name == "FailingDB")
                return new DatabasePipelineResult(cfg.Name, false, "Connection timeout", 0, 0, 0, null);
            return new DatabasePipelineResult(cfg.Name, true, null, 20, 15, 5, 75.0);
        });

        batchResult.DatabaseResults.Should().HaveCount(3);
        batchResult.DatabaseResults.Count(r => r.Success).Should().Be(2);
        batchResult.FailureLogs.Should().HaveCount(1);
        batchResult.FailureLogs[0].DatabaseName.Should().Be("FailingDB");
    }

    /// <summary>
    /// When only the last database fails, all preceding databases have correct results.
    /// </summary>
    [Fact]
    public void Last_database_failure_does_not_affect_preceding_results()
    {
        var configs = new List<BatchDatabaseConfig>
        {
            new("SuccessDB1", "Server=localhost;Database=SuccessDB1;", "session-s1", "scripts/setup-s1.sql"),
            new("SuccessDB2", "Server=localhost;Database=SuccessDB2;", "session-s2", "scripts/setup-s2.sql"),
            new("FailingDB", "Server=bad;Database=FailingDB;", "session-failing", "scripts/setup-failing.sql"),
        };

        var batchResult = ExecuteBatch(configs, cfg =>
        {
            if (cfg.Name == "FailingDB")
                throw new InvalidOperationException("Network path not found");
            return new DatabasePipelineResult(cfg.Name, true, null, 18, 14, 4, 77.8);
        });

        batchResult.DatabaseResults.Should().HaveCount(3);
        batchResult.DatabaseResults.Count(r => r.Success).Should().Be(2);
        batchResult.FailureLogs.Should().HaveCount(1);
        batchResult.FailureLogs[0].DatabaseName.Should().Be("FailingDB");

        // Verify preceding databases have correct results
        var s1 = batchResult.DatabaseResults.First(r => r.DatabaseName == "SuccessDB1");
        s1.ObjectCount.Should().Be(18);
        s1.PassCount.Should().Be(14);
    }

    #endregion
}

/// <summary>
/// Helper class for sequencing generators.
/// </summary>
internal static class GenHelpers
{
    /// <summary>
    /// Sequences a list of generators into a generator of lists.
    /// </summary>
    public static Gen<List<T>> Sequence<T>(List<Gen<T>> generators)
    {
        if (generators.Count == 0)
            return Gen.Constant(new List<T>());

        Gen<List<T>> result = Gen.Constant(new List<T>());
        foreach (var gen in generators)
        {
            result = from list in result
                     from item in gen
                     select new List<T>(list) { item };
        }
        return result;
    }
}
