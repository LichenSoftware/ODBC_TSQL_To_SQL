using FluentAssertions;
using FsCheck;
using FsCheck.Xunit;

namespace MigrationAssessment.Pipeline.PropertyTests;

/// <summary>
/// Feature: migration-validation-pipeline, Property 16: Validation Isolation
/// 
/// Validates: Requirements 6.5
///
/// Property 16: For any set of DDL objects where some fail validation, all other objects
/// SHALL still receive an independent pass/fail result — a failure in one object SHALL NOT
/// prevent validation of unrelated objects.
/// </summary>
public class ValidationIsolationPropertyTests
{
    #region Models

    /// <summary>
    /// Represents a DDL object submitted for PostgreSQL validation.
    /// </summary>
    public record DdlObject(
        string ObjectName,
        string ObjectType,
        string Ddl,
        bool IsValidSyntax // Simulates whether the DDL would pass or fail validation
    );

    /// <summary>
    /// Represents the validation result produced per object by the validator.
    /// </summary>
    public record ValidationResult(
        string ObjectName,
        string Status, // "pass" or "fail-syntax"
        string? ErrorMessage,
        string ValidationMode
    );

    #endregion

    #region Validation Isolation Validator (C# equivalent of Invoke-PgValidation.ps1 isolation logic)

    /// <summary>
    /// Simulates the validation isolation behavior from Invoke-PgValidation.ps1.
    /// Each DDL object is validated independently — a failure in one object does not
    /// prevent validation of other objects. Every input object receives a result.
    /// </summary>
    public static List<ValidationResult> ValidateWithIsolation(List<DdlObject> ddlObjects)
    {
        var results = new List<ValidationResult>();

        foreach (var obj in ddlObjects)
        {
            // Each object is validated independently in its own context
            // (mirrors the PowerShell script iterating over each object in topological order)
            var result = ValidateSingleObject(obj);
            results.Add(result);
        }

        return results;
    }

    /// <summary>
    /// Validates a single DDL object. If the DDL is valid syntax, it passes.
    /// If not, it fails with an error message. This mirrors the per-object
    /// validation logic in Invoke-SyntaxOnlyValidation / Invoke-LiveInstanceValidation.
    /// </summary>
    private static ValidationResult ValidateSingleObject(DdlObject obj)
    {
        if (string.IsNullOrWhiteSpace(obj.Ddl))
        {
            return new ValidationResult(
                obj.ObjectName,
                "fail-syntax",
                "DDL statement is empty or null",
                "syntax-only"
            );
        }

        if (obj.IsValidSyntax)
        {
            return new ValidationResult(
                obj.ObjectName,
                "pass",
                null,
                "syntax-only"
            );
        }
        else
        {
            return new ValidationResult(
                obj.ObjectName,
                "fail-syntax",
                $"Syntax error in DDL for {obj.ObjectName}",
                "syntax-only"
            );
        }
    }

    #endregion

    #region Generators

    private static readonly string[] ObjectTypes = { "Table", "View", "StoredProcedure", "Function", "Trigger" };

    /// <summary>
    /// Generates a valid PostgreSQL DDL statement.
    /// </summary>
    private static Gen<string> GenValidDdl()
    {
        return from objType in Gen.Elements("TABLE", "VIEW", "FUNCTION", "INDEX")
               from schema in Gen.Elements("public", "app", "data")
               from name in Gen.Elements("customers", "orders", "products", "users", "accounts")
               from suffix in Gen.Choose(1, 999)
               select objType switch
               {
                   "TABLE" => $"CREATE TABLE {schema}.{name}_{suffix} (id SERIAL PRIMARY KEY, name VARCHAR(100));",
                   "VIEW" => $"CREATE VIEW {schema}.vw_{name}_{suffix} AS SELECT 1 AS val;",
                   "FUNCTION" => $"CREATE OR REPLACE FUNCTION {schema}.fn_{name}_{suffix}() RETURNS INTEGER AS $$ BEGIN RETURN 1; END; $$ LANGUAGE plpgsql;",
                   _ => $"CREATE INDEX idx_{name}_{suffix} ON {schema}.{name}_{suffix} (id);"
               };
    }

    /// <summary>
    /// Generates an invalid DDL statement that would fail syntax validation.
    /// </summary>
    private static Gen<string> GenInvalidDdl()
    {
        return from objType in Gen.Elements("TABLE", "PROCEDURE", "FUNCTION")
               from name in Gen.Elements("bad_obj", "invalid_thing", "broken_ddl")
               from suffix in Gen.Choose(1, 999)
               select objType switch
               {
                   "TABLE" => $"CREATE TABLE {name}_{suffix} (id NVARCHAR(MAX), created DATETIME);", // T-SQL types
                   "PROCEDURE" => $"CREATE PROCEDURE {name}_{suffix} AS BEGIN SET NOCOUNT ON; END;", // T-SQL syntax
                   _ => $"DECLARE @x INT; SELECT @x = 1;" // T-SQL variable syntax
               };
    }

    /// <summary>
    /// Generates a set of DDL objects with a guaranteed mix of pass and fail outcomes.
    /// Each object name is unique via sequential index assignment.
    /// </summary>
    private static Gen<List<DdlObject>> GenMixedDdlObjects()
    {
        var genPassSpec = from objType in Gen.Elements(ObjectTypes)
                          from ddl in GenValidDdl()
                          select (objType, ddl, isValid: true);

        var genFailSpec = from objType in Gen.Elements(ObjectTypes)
                          from ddl in GenInvalidDdl()
                          select (objType, ddl, isValid: false);

        return from passCount in Gen.Choose(1, 15)
               from failCount in Gen.Choose(1, 15)
               from passSpecs in Gen.ListOf(passCount, genPassSpec)
               from failSpecs in Gen.ListOf(failCount, genFailSpec)
               let allSpecs = passSpecs.Concat(failSpecs).ToList()
               from shuffled in Gen.Shuffle(allSpecs.ToArray())
               let objects = shuffled.Select((spec, idx) =>
                   new DdlObject(
                       $"dbo.obj_{idx}",
                       spec.objType,
                       spec.ddl,
                       spec.isValid)).ToList()
               select objects;
    }

    /// <summary>
    /// Generates a set of DDL objects of arbitrary size (used for count verification).
    /// Each object name is unique via sequential index assignment.
    /// </summary>
    private static Gen<List<DdlObject>> GenArbitraryDdlObjects()
    {
        var genSpec = from objType in Gen.Elements(ObjectTypes)
                      from isValid in Gen.Elements(true, false)
                      from ddl in isValid ? GenValidDdl() : GenInvalidDdl()
                      select (objType, ddl, isValid);

        return from count in Gen.Choose(1, 30)
               from specs in Gen.ListOf(count, genSpec)
               let objects = specs.Select((spec, idx) =>
                   new DdlObject(
                       $"dbo.obj_{idx}",
                       spec.objType,
                       spec.ddl,
                       spec.isValid)).ToList()
               select objects;
    }

    #endregion

    #region Property 16: Validation Isolation Tests

    /// <summary>
    /// **Validates: Requirements 6.5**
    /// 
    /// For any set of DDL objects submitted for validation, the number of results returned
    /// SHALL equal the number of inputs — every object gets a result, none are dropped.
    /// </summary>
    [Property(MaxTest = 200)]
    public Property Every_input_object_receives_a_result()
    {
        return Prop.ForAll(GenArbitraryDdlObjects().ToArbitrary(), (List<DdlObject> ddlObjects) =>
        {
            var results = ValidateWithIsolation(ddlObjects);

            return (results.Count == ddlObjects.Count)
                .Label($"Expected {ddlObjects.Count} results but got {results.Count}");
        });
    }

    /// <summary>
    /// **Validates: Requirements 6.5**
    /// 
    /// For any set of DDL objects where some fail validation, every input object has a
    /// corresponding result with the same object name. No input is silently skipped.
    /// </summary>
    [Property(MaxTest = 200)]
    public Property Every_input_has_a_corresponding_result_by_name()
    {
        return Prop.ForAll(GenMixedDdlObjects().ToArbitrary(), (List<DdlObject> ddlObjects) =>
        {
            var results = ValidateWithIsolation(ddlObjects);

            var inputNames = ddlObjects.Select(o => o.ObjectName).ToList();
            var resultNames = results.Select(r => r.ObjectName).ToList();

            // Every input object name must appear in the results
            foreach (var name in inputNames)
            {
                if (!resultNames.Contains(name))
                {
                    return false.Label($"Input object '{name}' has no corresponding result");
                }
            }

            return true.Label("All input objects have corresponding results");
        });
    }

    /// <summary>
    /// **Validates: Requirements 6.5**
    /// 
    /// For any set of DDL objects where some fail validation, a failure in one object does NOT
    /// change another object's result. Specifically: objects that have valid DDL still pass
    /// regardless of how many other objects fail, and objects with invalid DDL still fail
    /// regardless of how many other objects pass.
    /// </summary>
    [Property(MaxTest = 200)]
    public Property Failure_in_one_object_does_not_affect_another_objects_result()
    {
        return Prop.ForAll(GenMixedDdlObjects().ToArbitrary(), (List<DdlObject> ddlObjects) =>
        {
            var results = ValidateWithIsolation(ddlObjects);

            // Build a lookup from object name to result
            var resultByName = results.ToDictionary(r => r.ObjectName, r => r);

            foreach (var obj in ddlObjects)
            {
                if (!resultByName.TryGetValue(obj.ObjectName, out var result))
                {
                    return false.Label($"Missing result for '{obj.ObjectName}'");
                }

                // An object's result should depend ONLY on its own DDL validity
                if (obj.IsValidSyntax)
                {
                    if (result.Status != "pass")
                    {
                        return false.Label(
                            $"Object '{obj.ObjectName}' has valid DDL but got status '{result.Status}' " +
                            $"(other failures may have interfered)");
                    }
                }
                else
                {
                    if (result.Status != "fail-syntax")
                    {
                        return false.Label(
                            $"Object '{obj.ObjectName}' has invalid DDL but got status '{result.Status}' " +
                            $"instead of 'fail-syntax'");
                    }
                }
            }

            return true.Label("Each object's result depends only on its own DDL validity");
        });
    }

    /// <summary>
    /// **Validates: Requirements 6.5**
    /// 
    /// Removing a failing object from the input set does not change the results of the
    /// remaining objects — demonstrating true isolation between objects.
    /// </summary>
    [Property(MaxTest = 200)]
    public Property Removing_a_failing_object_does_not_change_other_results()
    {
        return Prop.ForAll(GenMixedDdlObjects().ToArbitrary(), (List<DdlObject> ddlObjects) =>
        {
            // Validate the full set
            var fullResults = ValidateWithIsolation(ddlObjects);
            var fullResultByName = fullResults.ToDictionary(r => r.ObjectName, r => r);

            // Find a failing object to remove
            var failingObject = ddlObjects.FirstOrDefault(o => !o.IsValidSyntax);
            if (failingObject == null)
            {
                // No failing object in this input — property is trivially satisfied
                return true.Label("No failing objects to remove (trivially satisfied)");
            }

            // Remove the failing object and re-validate
            var reducedSet = ddlObjects.Where(o => o.ObjectName != failingObject.ObjectName).ToList();
            var reducedResults = ValidateWithIsolation(reducedSet);
            var reducedResultByName = reducedResults.ToDictionary(r => r.ObjectName, r => r);

            // Every remaining object should have the same result with or without the failing object
            foreach (var obj in reducedSet)
            {
                if (!fullResultByName.TryGetValue(obj.ObjectName, out var fullResult) ||
                    !reducedResultByName.TryGetValue(obj.ObjectName, out var reducedResult))
                {
                    return false.Label($"Missing result for '{obj.ObjectName}'");
                }

                if (fullResult.Status != reducedResult.Status)
                {
                    return false.Label(
                        $"Object '{obj.ObjectName}' changed from '{fullResult.Status}' to '{reducedResult.Status}' " +
                        $"after removing failing object '{failingObject.ObjectName}' — isolation violated");
                }
            }

            return true.Label("Removing a failing object does not change other results");
        });
    }

    /// <summary>
    /// **Validates: Requirements 6.5**
    /// 
    /// Adding a new failing object to an existing set does not change the results of
    /// the original objects — demonstrating isolation in the other direction.
    /// </summary>
    [Property(MaxTest = 200)]
    public Property Adding_a_failing_object_does_not_change_existing_results()
    {
        var genWithExtra = from objects in GenMixedDdlObjects()
                           from extraDdl in GenInvalidDdl()
                           from extraType in Gen.Elements(ObjectTypes)
                           // Use a name that cannot collide with the indexed originals (dbo.obj_N)
                           select (objects, new DdlObject("dbo.extra_failing_object_added", extraType, extraDdl, false));

        return Prop.ForAll(genWithExtra.ToArbitrary(), pair =>
        {
            var (originalObjects, extraFailingObject) = pair;

            // Validate the original set
            var originalResults = ValidateWithIsolation(originalObjects);
            var originalResultByName = originalResults.ToDictionary(r => r.ObjectName, r => r);

            // Add the extra failing object and validate the augmented set
            var augmentedObjects = originalObjects.Concat(new[] { extraFailingObject }).ToList();
            var augmentedResults = ValidateWithIsolation(augmentedObjects);
            var augmentedResultByName = augmentedResults.ToDictionary(r => r.ObjectName, r => r);

            // All original objects should have the same result
            foreach (var obj in originalObjects)
            {
                if (!originalResultByName.TryGetValue(obj.ObjectName, out var origResult) ||
                    !augmentedResultByName.TryGetValue(obj.ObjectName, out var augResult))
                {
                    return false.Label($"Missing result for '{obj.ObjectName}'");
                }

                if (origResult.Status != augResult.Status)
                {
                    return false.Label(
                        $"Object '{obj.ObjectName}' changed from '{origResult.Status}' to '{augResult.Status}' " +
                        $"after adding a failing object — isolation violated");
                }
            }

            // The extra failing object should also have a result
            if (!augmentedResultByName.ContainsKey(extraFailingObject.ObjectName))
            {
                return false.Label("Extra failing object has no result in augmented set");
            }

            return true.Label("Adding a failing object does not change existing results");
        });
    }

    #endregion
}
