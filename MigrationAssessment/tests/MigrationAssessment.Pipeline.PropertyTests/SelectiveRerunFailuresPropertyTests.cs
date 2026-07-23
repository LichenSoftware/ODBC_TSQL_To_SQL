using FluentAssertions;
using FsCheck;
using FsCheck.Xunit;

namespace MigrationAssessment.Pipeline.PropertyTests;

/// <summary>
/// Feature: migration-validation-pipeline, Property 9: Selective Re-run of Failures
///
/// Validates: Requirements 4.3
///
/// Property 9: For any session containing a mix of passed and failed objects, when the
/// Pipeline Runner is invoked in "rerun-failures" mode, it SHALL re-convert only objects
/// with status "fail-syntax" or "fail-convert" from the most recent Scoring Report, and
/// SHALL preserve all existing conversion results for objects with status "pass" or "skip".
/// </summary>
[Trait("Feature", "migration-validation-pipeline")]
[Trait("Property", "9: Selective Re-run of Failures")]
public class SelectiveRerunFailuresPropertyTests
{
    #region Data Models

    /// <summary>
    /// Represents the status of a migration object in a scoring report.
    /// Maps directly to the four valid statuses defined in Requirement 3.2 and
    /// the PowerShell pipeline's ObjectResults structure.
    /// </summary>
    public enum MigrationStatus
    {
        Pass,
        FailSyntax,
        FailConvert,
        Skip
    }

    /// <summary>
    /// A single object entry in the most recent Scoring Report.
    /// Mirrors the per-object shape stored in Scoring Report JSON:
    ///   { name, type, status, errorMessage, errorLineNumber, generatedDdl }
    /// </summary>
    public record ReportObject(
        string ObjectName,
        string ObjectType,
        string DatabaseName,
        MigrationStatus Status,
        string? GeneratedDdl,
        string? ErrorMessage,
        int? ErrorLineNumber
    );

    /// <summary>
    /// Result produced by re-converting a single failed object.
    /// After re-conversion the object has a new status (and new DDL/error).
    /// </summary>
    public record ReconversionResult(
        string ObjectName,
        MigrationStatus NewStatus,
        string? NewDdl,
        string? NewError
    );

    /// <summary>
    /// The merged object result after applying rerun-failures logic.
    /// Preserved objects keep their original ReportObject data;
    /// re-converted objects reflect the ReconversionResult data.
    /// </summary>
    public record MergedObject(
        string ObjectName,
        string ObjectType,
        string DatabaseName,
        MigrationStatus Status,
        string? GeneratedDdl,
        string? ErrorMessage,
        int? ErrorLineNumber,
        bool WasReConverted // true iff this object went through re-conversion
    );

    #endregion

    #region Rerun Logic (mirrors Invoke-RerunFailures in Run-MigrationPipeline.ps1)

    /// <summary>
    /// Implements the core selective re-run logic from <c>Invoke-RerunFailures</c>
    /// (Run-MigrationPipeline.ps1, Requirement 4.3).
    ///
    /// Algorithm:
    ///   1. Partition the previous report objects into:
    ///      - <em>failed</em>    objects: status == FailSyntax || FailConvert  → re-convert
    ///      - <em>preserved</em> objects: status == Pass || Skip               → keep as-is
    ///   2. For each failed object, apply its ReconversionResult (simulates convert + validate).
    ///   3. Merge preserved objects and re-converted objects into a single list.
    ///   4. Every input object appears exactly once in the merged output (nothing lost).
    /// </summary>
    public static List<MergedObject> ApplyRerunFailures(
        List<ReportObject> previousReportObjects,
        Dictionary<string, ReconversionResult> reconversionResults)
    {
        var merged = new List<MergedObject>();

        foreach (var obj in previousReportObjects)
        {
            bool isFailure = obj.Status == MigrationStatus.FailSyntax ||
                             obj.Status == MigrationStatus.FailConvert;

            if (isFailure && reconversionResults.TryGetValue(obj.ObjectName, out var newResult))
            {
                // Re-converted: use the new result
                merged.Add(new MergedObject(
                    obj.ObjectName,
                    obj.ObjectType,
                    obj.DatabaseName,
                    newResult.NewStatus,
                    newResult.NewDdl,
                    newResult.NewError,
                    ErrorLineNumber: null,
                    WasReConverted: true
                ));
            }
            else
            {
                // Preserved (pass/skip) or failed with no reconversion result provided
                merged.Add(new MergedObject(
                    obj.ObjectName,
                    obj.ObjectType,
                    obj.DatabaseName,
                    obj.Status,
                    obj.GeneratedDdl,
                    obj.ErrorMessage,
                    obj.ErrorLineNumber,
                    WasReConverted: false
                ));
            }
        }

        return merged;
    }

    #endregion

    #region Generators

    private static readonly string[] ValidObjectTypes =
        { "Table", "View", "StoredProcedure", "Function", "Trigger" };

    private static readonly string[] DatabaseNames =
        { "ProcedureComplexityDB", "ViewsTriggerDB", "TypesAndCLRDB", "CrossSchemaAdvancedDB" };

    /// <summary>
    /// Generates a random object name in schema.object format.
    /// </summary>
    private static Gen<string> GenObjectName(string prefix = "obj")
    {
        return from schema in Gen.Elements("dbo", "sales", "hr", "inventory")
               from suffix in Gen.Choose(1, 9999)
               select $"{schema}.{prefix}_{suffix}";
    }

    /// <summary>
    /// Generates a single ReportObject with a given status.
    /// </summary>
    private static Gen<ReportObject> GenReportObject(MigrationStatus status)
    {
        return from objectName in GenObjectName()
               from objectType in Gen.Elements(ValidObjectTypes)
               from dbName in Gen.Elements(DatabaseNames)
               from ddl in status == MigrationStatus.FailConvert
                   ? Gen.Constant<string?>(null)
                   : Gen.Elements<string?>(
                       "CREATE TABLE dbo.t1 (id INT PRIMARY KEY);",
                       "CREATE OR REPLACE FUNCTION dbo.fn1() RETURNS INT AS $$ BEGIN RETURN 1; END; $$ LANGUAGE plpgsql;",
                       "CREATE VIEW dbo.vw1 AS SELECT 1 AS val;")
               from errorMsg in (status == MigrationStatus.FailSyntax || status == MigrationStatus.FailConvert)
                   ? Gen.Elements<string?>(
                       "syntax error at or near \"DECLARE\"",
                       "type \"hierarchyid\" does not exist",
                       "function \"ISNULL\"() does not exist",
                       "Conversion produced no DDL output")
                   : Gen.Constant<string?>(null)
               from lineNum in (status == MigrationStatus.FailSyntax)
                   ? Gen.Choose(1, 50).Select(n => (int?)n)
                   : Gen.Constant<int?>(null)
               select new ReportObject(objectName, objectType, dbName, status, ddl, errorMsg, lineNum);
    }

    /// <summary>
    /// Generates a session (list of ReportObjects) with a mix of all four statuses.
    /// Guarantees at least one object of each status type.
    /// </summary>
    private static Gen<List<ReportObject>> GenMixedSession()
    {
        return from passCount in Gen.Choose(1, 10)
               from failSyntaxCount in Gen.Choose(1, 10)
               from failConvertCount in Gen.Choose(1, 10)
               from skipCount in Gen.Choose(1, 10)
               from passObjects in Gen.ListOf(passCount, GenReportObject(MigrationStatus.Pass))
               from failSyntaxObjects in Gen.ListOf(failSyntaxCount, GenReportObject(MigrationStatus.FailSyntax))
               from failConvertObjects in Gen.ListOf(failConvertCount, GenReportObject(MigrationStatus.FailConvert))
               from skipObjects in Gen.ListOf(skipCount, GenReportObject(MigrationStatus.Skip))
               // Assign unique names via sequential index to avoid collisions
               let allRaw = passObjects
                   .Concat(failSyntaxObjects)
                   .Concat(failConvertObjects)
                   .Concat(skipObjects)
                   .Select((obj, i) => obj with { ObjectName = $"{obj.DatabaseName}.obj_{i:D4}" })
                   .ToList()
               select allRaw;
    }

    /// <summary>
    /// Generates reconversion results for all failed objects in the session.
    /// Each failed object gets a new status (could pass or fail again).
    /// </summary>
    private static Gen<Dictionary<string, ReconversionResult>> GenReconversionResults(
        List<ReportObject> sessionObjects)
    {
        var failedObjects = sessionObjects
            .Where(o => o.Status == MigrationStatus.FailSyntax ||
                        o.Status == MigrationStatus.FailConvert)
            .ToList();

        if (failedObjects.Count == 0)
        {
            return Gen.Constant(new Dictionary<string, ReconversionResult>());
        }

        // Generate a random new status for each failed object
        var genNewStatus = Gen.Elements(
            MigrationStatus.Pass,
            MigrationStatus.FailSyntax,
            MigrationStatus.FailConvert);

        var genNewDdl = Gen.Elements<string?>(
            "CREATE TABLE dbo.repaired (id INT PRIMARY KEY);",
            "CREATE OR REPLACE FUNCTION dbo.fn_fixed() RETURNS INT AS $$ BEGIN RETURN 0; END; $$ LANGUAGE plpgsql;",
            null);

        var genNewError = Gen.Elements<string?>(
            null,
            "syntax error at or near \"BEGIN\"",
            "type \"xml\" does not exist");

        // Build one Gen<Dictionary> by generating a result per failed object
        Gen<Dictionary<string, ReconversionResult>> dictGen =
            Gen.Constant(new Dictionary<string, ReconversionResult>());

        foreach (var failedObj in failedObjects)
        {
            var capturedName = failedObj.ObjectName;
            var resultGen = from newStatus in genNewStatus
                            from newDdl in genNewDdl
                            from newError in genNewError
                            select new ReconversionResult(capturedName, newStatus, newDdl, newError);

            dictGen = from dict in dictGen
                      from result in resultGen
                      select new Dictionary<string, ReconversionResult>(dict) { [capturedName] = result };
        }

        return dictGen;
    }

    /// <summary>
    /// Bundles a session and its reconversion results for property test input.
    /// </summary>
    private static Gen<(List<ReportObject> Session, Dictionary<string, ReconversionResult> Reconversions)>
        GenSessionWithReconversions()
    {
        return GenMixedSession().SelectMany(session =>
            GenReconversionResults(session).Select(reconversions => (session, reconversions)));
    }

    #endregion

    #region Property 9 Tests

    /// <summary>
    /// Property 9.1: Only objects with status "fail-syntax" or "fail-convert" are re-converted.
    /// Objects with status "pass" or "skip" are never marked as re-converted.
    ///
    /// <b>Validates: Requirements 4.3</b>
    /// </summary>
    [Property(MaxTest = 100)]
    public Property Only_failed_objects_are_re_converted()
    {
        return Prop.ForAll(
            GenSessionWithReconversions().ToArbitrary(),
            input =>
            {
                var (session, reconversions) = input;
                var merged = ApplyRerunFailures(session, reconversions);

                // Every object that was originally pass or skip must NOT be re-converted
                var passAndSkipNames = session
                    .Where(o => o.Status == MigrationStatus.Pass || o.Status == MigrationStatus.Skip)
                    .Select(o => o.ObjectName)
                    .ToHashSet();

                foreach (var mergedObj in merged.Where(m => passAndSkipNames.Contains(m.ObjectName)))
                {
                    mergedObj.WasReConverted.Should().BeFalse(
                        $"object '{mergedObj.ObjectName}' had status pass/skip and must NOT be re-converted");
                }

                return true.ToProperty();
            });
    }

    /// <summary>
    /// Property 9.2: All objects with status "pass" or "skip" are preserved unchanged —
    /// their status, DDL, error message, and line number remain exactly the same.
    ///
    /// <b>Validates: Requirements 4.3</b>
    /// </summary>
    [Property(MaxTest = 100)]
    public Property Pass_and_skip_objects_are_preserved_unchanged()
    {
        return Prop.ForAll(
            GenSessionWithReconversions().ToArbitrary(),
            input =>
            {
                var (session, reconversions) = input;
                var merged = ApplyRerunFailures(session, reconversions);

                var mergedByName = merged.ToDictionary(m => m.ObjectName);

                var preservedObjects = session
                    .Where(o => o.Status == MigrationStatus.Pass || o.Status == MigrationStatus.Skip);

                foreach (var original in preservedObjects)
                {
                    mergedByName.Should().ContainKey(original.ObjectName,
                        $"preserved object '{original.ObjectName}' must appear in the merged result");

                    var mergedObj = mergedByName[original.ObjectName];

                    mergedObj.Status.Should().Be(original.Status,
                        $"preserved object '{original.ObjectName}' status must remain '{original.Status}'");

                    mergedObj.GeneratedDdl.Should().Be(original.GeneratedDdl,
                        $"preserved object '{original.ObjectName}' DDL must remain unchanged");

                    mergedObj.ErrorMessage.Should().Be(original.ErrorMessage,
                        $"preserved object '{original.ObjectName}' error message must remain unchanged");

                    mergedObj.ErrorLineNumber.Should().Be(original.ErrorLineNumber,
                        $"preserved object '{original.ObjectName}' error line number must remain unchanged");
                }

                return true.ToProperty();
            });
    }

    /// <summary>
    /// Property 9.3: The merged results contain every object from the original session —
    /// nothing is lost and no object count is reduced.
    ///
    /// <b>Validates: Requirements 4.3</b>
    /// </summary>
    [Property(MaxTest = 100)]
    public Property Merged_results_contain_every_original_object()
    {
        return Prop.ForAll(
            GenSessionWithReconversions().ToArbitrary(),
            input =>
            {
                var (session, reconversions) = input;
                var merged = ApplyRerunFailures(session, reconversions);

                // Merged count must equal original count
                merged.Count.Should().Be(session.Count,
                    $"merged output ({merged.Count}) must contain same count as original session ({session.Count})");

                // Every original object name must appear in the merged result
                var mergedNames = merged.Select(m => m.ObjectName).ToHashSet();
                foreach (var original in session)
                {
                    mergedNames.Should().Contain(original.ObjectName,
                        $"original object '{original.ObjectName}' is missing from the merged result");
                }

                return true.ToProperty();
            });
    }

    /// <summary>
    /// Property 9.4: After re-run, the merged result reflects the new reconversion outcome
    /// for every previously-failed object.  Specifically, a re-converted object's status,
    /// DDL, and error in the merged output match the ReconversionResult, not the original
    /// report entry.
    ///
    /// <b>Validates: Requirements 4.3</b>
    /// </summary>
    [Property(MaxTest = 100)]
    public Property Re_converted_objects_reflect_new_reconversion_outcome()
    {
        return Prop.ForAll(
            GenSessionWithReconversions().ToArbitrary(),
            input =>
            {
                var (session, reconversions) = input;
                var merged = ApplyRerunFailures(session, reconversions);

                var mergedByName = merged.ToDictionary(m => m.ObjectName);

                foreach (var (objectName, reconResult) in reconversions)
                {
                    mergedByName.Should().ContainKey(objectName,
                        $"re-converted object '{objectName}' must appear in the merged result");

                    var mergedObj = mergedByName[objectName];

                    mergedObj.WasReConverted.Should().BeTrue(
                        $"object '{objectName}' was re-converted and must be flagged as such");

                    mergedObj.Status.Should().Be(reconResult.NewStatus,
                        $"re-converted object '{objectName}' must have new status '{reconResult.NewStatus}'");

                    mergedObj.GeneratedDdl.Should().Be(reconResult.NewDdl,
                        $"re-converted object '{objectName}' must carry the new DDL from reconversion");

                    mergedObj.ErrorMessage.Should().Be(reconResult.NewError,
                        $"re-converted object '{objectName}' must carry the new error from reconversion");
                }

                return true.ToProperty();
            });
    }

    /// <summary>
    /// Property 9.5: When there are no failed objects in the report (all pass/skip),
    /// the merged output is identical to the input — nothing is re-converted.
    ///
    /// <b>Validates: Requirements 4.3</b>
    /// </summary>
    [Property(MaxTest = 100)]
    public Property No_failed_objects_means_nothing_is_re_converted()
    {
        var genPassSkipOnly = from passCount in Gen.Choose(1, 15)
                              from skipCount in Gen.Choose(0, 5)
                              from passObjects in Gen.ListOf(passCount, GenReportObject(MigrationStatus.Pass))
                              from skipObjects in Gen.ListOf(skipCount, GenReportObject(MigrationStatus.Skip))
                              let allRaw = passObjects
                                  .Concat(skipObjects)
                                  .Select((obj, i) => obj with { ObjectName = $"{obj.DatabaseName}.pso_{i:D4}" })
                                  .ToList()
                              select allRaw;

        return Prop.ForAll(
            genPassSkipOnly.ToArbitrary(),
            session =>
            {
                var emptyReconversions = new Dictionary<string, ReconversionResult>();
                var merged = ApplyRerunFailures(session, emptyReconversions);

                merged.Count.Should().Be(session.Count,
                    "no objects should be dropped when nothing needs re-conversion");

                merged.Should().NotContain(m => m.WasReConverted,
                    "no objects should be flagged as re-converted when there are no failures");

                // All statuses must be unchanged
                var mergedByName = merged.ToDictionary(m => m.ObjectName);
                foreach (var original in session)
                {
                    mergedByName[original.ObjectName].Status.Should().Be(original.Status,
                        $"object '{original.ObjectName}' status must be unchanged when not re-converted");
                }

                return true.ToProperty();
            });
    }

    /// <summary>
    /// Property 9.6: The set of object names in the merged output is identical to the set
    /// in the original report — no objects are added or removed.
    ///
    /// <b>Validates: Requirements 4.3</b>
    /// </summary>
    [Property(MaxTest = 100)]
    public Property Merged_object_set_is_identical_to_original_set()
    {
        return Prop.ForAll(
            GenSessionWithReconversions().ToArbitrary(),
            input =>
            {
                var (session, reconversions) = input;
                var merged = ApplyRerunFailures(session, reconversions);

                var originalNames = session.Select(o => o.ObjectName).OrderBy(n => n).ToList();
                var mergedNames = merged.Select(m => m.ObjectName).OrderBy(n => n).ToList();

                mergedNames.Should().BeEquivalentTo(originalNames,
                    "the set of object names in merged output must exactly match the original report");

                return true.ToProperty();
            });
    }

    #endregion

    #region Edge Case Tests

    /// <summary>
    /// Edge case: A session where all objects are "fail-syntax" — every object is re-converted,
    /// none are preserved.
    /// </summary>
    [Fact]
    public void All_fail_syntax_objects_are_re_converted_none_preserved()
    {
        var session = new List<ReportObject>
        {
            new("dbo.sp_A", "StoredProcedure", "TestDB", MigrationStatus.FailSyntax,
                "CREATE OR REPLACE FUNCTION...", "syntax error at or near \"DECLARE\"", 5),
            new("dbo.sp_B", "StoredProcedure", "TestDB", MigrationStatus.FailSyntax,
                "CREATE OR REPLACE FUNCTION...", "type \"hierarchyid\" does not exist", 12),
        };

        var reconversions = new Dictionary<string, ReconversionResult>
        {
            ["dbo.sp_A"] = new("dbo.sp_A", MigrationStatus.Pass, "CREATE OR REPLACE FUNCTION dbo.sp_A() RETURNS void AS $$ BEGIN END; $$ LANGUAGE plpgsql;", null),
            ["dbo.sp_B"] = new("dbo.sp_B", MigrationStatus.FailSyntax, "CREATE OR REPLACE FUNCTION dbo.sp_B()...", "type \"uuid\" does not exist"),
        };

        var merged = ApplyRerunFailures(session, reconversions);

        merged.Should().HaveCount(2);
        merged.Should().NotContain(m => !m.WasReConverted,
            "all objects were failed, so all must be re-converted");

        merged.First(m => m.ObjectName == "dbo.sp_A").Status.Should().Be(MigrationStatus.Pass);
        merged.First(m => m.ObjectName == "dbo.sp_B").Status.Should().Be(MigrationStatus.FailSyntax);
    }

    /// <summary>
    /// Edge case: A session where all objects are "pass" — nothing is re-converted.
    /// </summary>
    [Fact]
    public void All_pass_objects_are_preserved_nothing_re_converted()
    {
        var session = new List<ReportObject>
        {
            new("dbo.tbl_Orders", "Table", "TestDB", MigrationStatus.Pass,
                "CREATE TABLE dbo.tbl_Orders (id INT);", null, null),
            new("dbo.vw_Summary", "View", "TestDB", MigrationStatus.Pass,
                "CREATE VIEW dbo.vw_Summary AS SELECT 1;", null, null),
        };

        var reconversions = new Dictionary<string, ReconversionResult>(); // empty — no failures

        var merged = ApplyRerunFailures(session, reconversions);

        merged.Should().HaveCount(2);
        merged.Should().NotContain(m => m.WasReConverted,
            "all objects have status 'pass', so none should be re-converted");

        merged.All(m => m.Status == MigrationStatus.Pass).Should().BeTrue(
            "all pass objects must remain pass");
    }

    /// <summary>
    /// Edge case: A session where all objects are "skip" — nothing is re-converted,
    /// all statuses are preserved as skip.
    /// </summary>
    [Fact]
    public void All_skip_objects_are_preserved_nothing_re_converted()
    {
        var session = new List<ReportObject>
        {
            new("dbo.syn_ActiveCustomers", "Synonym", "TestDB", MigrationStatus.Skip, null, null, null),
            new("dbo.seq_OrderId", "Sequence", "TestDB", MigrationStatus.Skip, null, null, null),
        };

        var reconversions = new Dictionary<string, ReconversionResult>();

        var merged = ApplyRerunFailures(session, reconversions);

        merged.Should().HaveCount(2);
        merged.Should().NotContain(m => m.WasReConverted);
        merged.All(m => m.Status == MigrationStatus.Skip).Should().BeTrue();
    }

    /// <summary>
    /// Edge case: A re-converted object that still fails after re-conversion is included
    /// in the merged result with its new (still-failed) status — it is not silently dropped.
    /// </summary>
    [Fact]
    public void Still_failed_re_converted_object_is_included_in_merged_result()
    {
        var session = new List<ReportObject>
        {
            new("dbo.sp_Hard", "StoredProcedure", "TestDB", MigrationStatus.FailConvert,
                null, "Conversion produced no DDL output", null),
            new("dbo.tbl_Users", "Table", "TestDB", MigrationStatus.Pass,
                "CREATE TABLE dbo.tbl_Users (id INT);", null, null),
        };

        var reconversions = new Dictionary<string, ReconversionResult>
        {
            ["dbo.sp_Hard"] = new("dbo.sp_Hard", MigrationStatus.FailSyntax,
                "CREATE OR REPLACE FUNCTION dbo.sp_Hard()...", "syntax error at or near \"DECLARE\""),
        };

        var merged = ApplyRerunFailures(session, reconversions);

        merged.Should().HaveCount(2,
            "merged result must still contain both objects even if re-conversion produced another failure");

        var hardObj = merged.First(m => m.ObjectName == "dbo.sp_Hard");
        hardObj.Status.Should().Be(MigrationStatus.FailSyntax,
            "re-converted object that still fails must use the new failure status");
        hardObj.WasReConverted.Should().BeTrue();

        var usersObj = merged.First(m => m.ObjectName == "dbo.tbl_Users");
        usersObj.Status.Should().Be(MigrationStatus.Pass,
            "the preserved pass object must remain pass");
        usersObj.WasReConverted.Should().BeFalse();
    }

    #endregion
}
