using FluentAssertions;
using FsCheck;
using FsCheck.Xunit;

namespace MigrationAssessment.Pipeline.PropertyTests;

/// <summary>
/// Feature: migration-validation-pipeline, Property 7: Failure Diagnostics Completeness
/// 
/// Validates: Requirements 4.1
///
/// Property 7: For any object with status "fail-syntax" or "fail-convert", the Scoring Report
/// SHALL include the specific PostgreSQL syntax error message, the line number where parsing
/// failed, and the full generated DDL text for that object.
/// </summary>
public class FailureDiagnosticsCompletenessPropertyTests
{
    private static readonly string[] ValidObjectTypes = { "StoredProcedure", "Function", "View", "Table", "Trigger" };
    private static readonly string[] FailureStatuses = { "fail-syntax", "fail-convert" };

    /// <summary>
    /// Sample error messages representing various failure scenarios.
    /// These are realistic PostgreSQL error messages that would occur during validation.
    /// </summary>
    private static readonly string[] SampleErrorMessages =
    {
        "syntax error at or near \"DECLARE\"",
        "type \"hierarchyid\" does not exist",
        "function \"dbo.fn_FormatDate\" does not exist",
        "relation \"dbo.Orders\" does not exist",
        "operator does not exist: varchar + integer",
        "ERROR in PL/pgSQL function: syntax error at RETURN",
        "unterminated block at END",
        "column \"user_id\" type \"uniqueidentifier\" does not exist",
        "no function matches the given name and argument types",
        "cannot cast type integer to hierarchyid",
        "schema \"audit\" does not exist",
        "view \"dbo.vw_Summary\" does not exist",
        "table \"dbo.TempResults\" does not exist",
        "unrecognized data type: datetime2",
        "undefined function: ISNULL"
    };

    /// <summary>
    /// Sample DDL text fragments representing generated DDL output.
    /// </summary>
    private static readonly string[] SampleDdlFragments =
    {
        "CREATE OR REPLACE FUNCTION dbo.sp_ProcessOrder() RETURNS void AS $$ BEGIN",
        "CREATE TABLE dbo.Orders (id SERIAL PRIMARY KEY, customer_id INT NOT NULL);",
        "CREATE OR REPLACE PROCEDURE dbo.sp_UpdateStock() LANGUAGE plpgsql AS $$ DECLARE v_count INT; BEGIN",
        "CREATE VIEW dbo.vw_RecentOrders AS SELECT * FROM orders WHERE order_date > NOW() - INTERVAL '30 days';",
        "DO $$ BEGIN RAISE NOTICE 'Processing'; END $$;",
        "CREATE OR REPLACE FUNCTION dbo.fn_GetTotal(p_id INT) RETURNS NUMERIC AS $$ BEGIN RETURN 0; END $$ LANGUAGE plpgsql;",
        "CREATE TRIGGER tr_audit AFTER INSERT ON orders FOR EACH ROW EXECUTE FUNCTION audit_insert();",
        "ALTER TABLE products ADD COLUMN metadata JSONB DEFAULT '{}'::jsonb;"
    };

    #region Generators

    /// <summary>
    /// Generates a non-empty, non-whitespace error message string.
    /// </summary>
    private static Gen<string> GenNonEmptyErrorMessage()
    {
        return Gen.OneOf(
            Gen.Elements(SampleErrorMessages),
            from prefix in Gen.Elements("syntax error at or near", "ERROR:", "cannot", "undefined", "unknown")
            from suffix in Gen.Elements("\"DECLARE\"", "\"BEGIN\"", "type", "function", "relation", "column")
            from lineInfo in Gen.Elements("", " at line 5", " near position 42")
            select $"{prefix} {suffix}{lineInfo}"
        );
    }

    /// <summary>
    /// Generates a non-empty, non-whitespace DDL string.
    /// </summary>
    private static Gen<string> GenNonEmptyDdl()
    {
        return Gen.OneOf(
            Gen.Elements(SampleDdlFragments),
            from keyword in Gen.Elements("CREATE OR REPLACE FUNCTION", "CREATE TABLE", "CREATE VIEW", "CREATE TRIGGER", "CREATE OR REPLACE PROCEDURE")
            from schema in Gen.Elements("dbo", "public", "audit", "sales")
            from name in Gen.Elements("sp_Process", "fn_Calculate", "vw_Summary", "tbl_Data", "tr_Audit")
            from body in Gen.Elements(
                "() RETURNS void AS $$ BEGIN NULL; END $$ LANGUAGE plpgsql;",
                " (id SERIAL PRIMARY KEY);",
                " AS SELECT 1;",
                " AFTER INSERT ON t FOR EACH ROW EXECUTE FUNCTION f();")
            select $"{keyword} {schema}.{name}{body}"
        );
    }

    /// <summary>
    /// Generates a positive line number for the error location.
    /// </summary>
    private static Gen<int> GenLineNumber()
    {
        return Gen.Choose(1, 500);
    }

    /// <summary>
    /// Generates a single FailedObject with guaranteed non-empty error message,
    /// positive line number, and non-empty DDL.
    /// </summary>
    private static Gen<FailedObject> GenFailedObject()
    {
        return from objType in Gen.Elements(ValidObjectTypes)
               from status in Gen.Elements(FailureStatuses)
               from errorMessage in GenNonEmptyErrorMessage()
               from lineNumber in GenLineNumber()
               from ddl in GenNonEmptyDdl()
               from schema in Gen.Elements("dbo", "sales", "audit", "hr")
               from name in Gen.Elements("sp_Process", "fn_Calculate", "vw_Report", "tbl_Data", "tr_Log")
               from idx in Gen.Choose(1, 1000)
               select new FailedObject(
                   ObjectName: $"{schema}.{name}_{idx}",
                   ObjectType: objType,
                   Status: status,
                   ErrorMessage: errorMessage,
                   ErrorLineNumber: lineNumber,
                   GeneratedDdl: ddl
               );
    }

    /// <summary>
    /// Generates a list of 1 to 30 failed objects.
    /// </summary>
    private static Gen<List<FailedObject>> GenFailedObjects()
    {
        return from count in Gen.Choose(1, 30)
               from objects in Gen.ListOf(count, GenFailedObject())
               select objects.ToList();
    }

    #endregion

    /// <summary>
    /// **Validates: Requirements 4.1**
    /// 
    /// For any set of failed objects, every failed object in the diagnostics output
    /// shall have a non-null, non-empty errorMessage in the details.
    /// </summary>
    [Property(MaxTest = 100)]
    public Property Every_Failed_Object_Has_ErrorMessage_In_Report()
    {
        return Prop.ForAll(GenFailedObjects().ToArbitrary(), (List<FailedObject> failedObjects) =>
        {
            var categories = DiagnosticsClassifier.Classify(failedObjects);

            // Flatten all details from all categories
            var allDetails = categories.SelectMany(c => c.Details).ToList();

            // Total details should equal total input objects
            allDetails.Count.Should().Be(failedObjects.Count,
                "every failed object must appear exactly once in the diagnostics output");

            // Every detail must have a non-null, non-empty error message
            foreach (var detail in allDetails)
            {
                detail.ErrorMessage.Should().NotBeNullOrEmpty(
                    "the Scoring Report SHALL include the specific PostgreSQL syntax error message for each failed object");
            }
        });
    }

    /// <summary>
    /// **Validates: Requirements 4.1**
    /// 
    /// For any set of failed objects, every failed object in the diagnostics output
    /// shall have a non-null errorLineNumber in the details.
    /// </summary>
    [Property(MaxTest = 100)]
    public Property Every_Failed_Object_Has_LineNumber_In_Report()
    {
        return Prop.ForAll(GenFailedObjects().ToArbitrary(), (List<FailedObject> failedObjects) =>
        {
            var categories = DiagnosticsClassifier.Classify(failedObjects);

            // Flatten all details from all categories
            var allDetails = categories.SelectMany(c => c.Details).ToList();

            // Total details should equal total input objects
            allDetails.Count.Should().Be(failedObjects.Count,
                "every failed object must appear exactly once in the diagnostics output");

            // Every detail must have a line number
            foreach (var detail in allDetails)
            {
                detail.LineNumber.Should().NotBeNull(
                    "the Scoring Report SHALL include the line number where parsing failed for each failed object");
                detail.LineNumber!.Value.Should().BeGreaterThan(0,
                    "line numbers should be positive integers");
            }
        });
    }

    /// <summary>
    /// **Validates: Requirements 4.1**
    /// 
    /// For any set of failed objects, every failed object in the diagnostics output
    /// shall have non-null, non-empty generatedDdl in the details.
    /// </summary>
    [Property(MaxTest = 100)]
    public Property Every_Failed_Object_Has_GeneratedDdl_In_Report()
    {
        return Prop.ForAll(GenFailedObjects().ToArbitrary(), (List<FailedObject> failedObjects) =>
        {
            var categories = DiagnosticsClassifier.Classify(failedObjects);

            // Flatten all details from all categories
            var allDetails = categories.SelectMany(c => c.Details).ToList();

            // Total details should equal total input objects
            allDetails.Count.Should().Be(failedObjects.Count,
                "every failed object must appear exactly once in the diagnostics output");

            // Every detail must have non-null, non-empty DDL text
            foreach (var detail in allDetails)
            {
                detail.Ddl.Should().NotBeNullOrEmpty(
                    "the Scoring Report SHALL include the full generated DDL text for each failed object");
            }
        });
    }

    /// <summary>
    /// **Validates: Requirements 4.1**
    /// 
    /// For any set of failed objects, all three diagnostic fields (errorMessage, lineNumber,
    /// generatedDdl) must be present simultaneously for every failure. This is the combined
    /// completeness check ensuring no partial diagnostic records exist.
    /// </summary>
    [Property(MaxTest = 100)]
    public Property Every_Failed_Object_Has_Complete_Diagnostics()
    {
        return Prop.ForAll(GenFailedObjects().ToArbitrary(), (List<FailedObject> failedObjects) =>
        {
            var categories = DiagnosticsClassifier.Classify(failedObjects);

            // Flatten all details from all categories
            var allDetails = categories.SelectMany(c => c.Details).ToList();

            // Total number of details must equal the input count
            allDetails.Count.Should().Be(failedObjects.Count,
                "every input failed object must produce exactly one detail entry in the output");

            // Every detail must have all three required fields populated
            for (int i = 0; i < allDetails.Count; i++)
            {
                var detail = allDetails[i];

                detail.ErrorMessage.Should().NotBeNullOrEmpty(
                    $"detail at index {i}: errorMessage must be non-null and non-empty");

                detail.LineNumber.Should().NotBeNull(
                    $"detail at index {i}: lineNumber must be non-null");

                detail.Ddl.Should().NotBeNullOrEmpty(
                    $"detail at index {i}: generatedDdl must be non-null and non-empty");
            }
        });
    }
}
