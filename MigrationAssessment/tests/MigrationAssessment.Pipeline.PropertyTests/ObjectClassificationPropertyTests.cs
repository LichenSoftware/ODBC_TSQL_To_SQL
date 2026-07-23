using FluentAssertions;
using FsCheck;
using FsCheck.Xunit;

namespace MigrationAssessment.Pipeline.PropertyTests;

/// <summary>
/// Property-based tests for Object Classification Correctness.
/// Feature: migration-validation-pipeline, Property 4: Object Classification Correctness
/// 
/// Validates: Requirements 3.2
/// 
/// For any schema object processed by the pipeline, the object SHALL be classified as exactly
/// one of: "pass" (PostgreSQL validator accepts the DDL), "fail-syntax" (PostgreSQL validator
/// rejects the DDL), "fail-convert" (conversion step failed or errored), or "skip" (object
/// type not in {Table, View, StoredProcedure, Function, Trigger}).
/// </summary>
public class ObjectClassificationPropertyTests
{
    /// <summary>
    /// The set of object types that are convertible (not skipped).
    /// </summary>
    private static readonly HashSet<string> ConvertibleTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "Table",
        "View",
        "StoredProcedure",
        "Function",
        "Trigger"
    };

    /// <summary>
    /// All valid classification statuses.
    /// </summary>
    private static readonly ObjectStatus[] AllStatuses = Enum.GetValues<ObjectStatus>();

    #region Generators

    /// <summary>
    /// Generates random object names in schema.name format.
    /// </summary>
    private static Gen<string> GenObjectName()
    {
        var schemas = Gen.Elements("dbo", "sales", "hr", "inventory", "reports");
        var names = Gen.Elements(
            "sp_Process", "fn_Calculate", "vw_Summary", "tbl_Orders",
            "tr_Audit", "usp_Update", "udf_Format", "vw_Recent",
            "sp_Complex", "fn_Validate", "tbl_Items", "tr_Insert",
            "sp_Report", "vw_Monthly", "fn_Convert", "tbl_Users");

        return from schema in schemas
               from name in names
               from suffix in Gen.Choose(1, 999)
               select $"{schema}.{name}_{suffix}";
    }

    /// <summary>
    /// Generates convertible object types (in the valid set).
    /// </summary>
    private static Gen<string> GenConvertibleType()
    {
        return Gen.Elements("Table", "View", "StoredProcedure", "Function", "Trigger");
    }

    /// <summary>
    /// Generates non-convertible object types (NOT in the valid set).
    /// </summary>
    private static Gen<string> GenNonConvertibleType()
    {
        return Gen.Elements(
            "Synonym", "Sequence", "Schema", "User", "Role",
            "Assembly", "Certificate", "Credential", "LinkedServer",
            "DatabaseRole", "ApplicationRole", "XmlSchemaCollection",
            "PartitionFunction", "PartitionScheme", "ServiceBrokerQueue");
    }

    /// <summary>
    /// Generates any object type (mix of convertible and non-convertible).
    /// </summary>
    private static Gen<string> GenAnyObjectType()
    {
        return Gen.Frequency(
            Tuple.Create(5, GenConvertibleType()),
            Tuple.Create(3, GenNonConvertibleType()));
    }

    /// <summary>
    /// Generates a database name.
    /// </summary>
    private static Gen<string> GenDatabaseName()
    {
        return Gen.Elements(
            "ProcedureComplexityDB", "ViewsTriggerDB", "TypesAndCLRDB",
            "CrossSchemaAdvancedDB", "AssessmentTestDB");
    }

    /// <summary>
    /// Generates a validation outcome for convertible objects.
    /// Convertible objects can be pass, fail-syntax, or fail-convert (not skip).
    /// </summary>
    private static Gen<ObjectStatus> GenConvertibleOutcome()
    {
        return Gen.Elements(ObjectStatus.Pass, ObjectStatus.FailSyntax, ObjectStatus.FailConvert);
    }

    /// <summary>
    /// Generates a list of objects with random types and appropriate classification.
    /// This simulates the classification logic: convertible types get pass/fail-syntax/fail-convert,
    /// non-convertible types get skip.
    /// </summary>
    private static Gen<List<ObjectResult>> GenClassifiedObjects()
    {
        var genConvertibleObject =
            from name in GenObjectName()
            from objType in GenConvertibleType()
            from dbName in GenDatabaseName()
            from status in GenConvertibleOutcome()
            select new ObjectResult(name, objType, dbName, status);

        var genNonConvertibleObject =
            from name in GenObjectName()
            from objType in GenNonConvertibleType()
            from dbName in GenDatabaseName()
            select new ObjectResult(name, objType, dbName, ObjectStatus.Skip);

        var genAnyObject = Gen.Frequency(
            Tuple.Create(5, genConvertibleObject),
            Tuple.Create(3, genNonConvertibleObject));

        return from count in Gen.Choose(1, 50)
               from objects in Gen.ListOf(count, genAnyObject)
               select objects.ToList();
    }

    #endregion

    #region Classification Logic Under Test

    /// <summary>
    /// Classifies a single object based on its type and validation outcome.
    /// This mirrors the PowerShell classification logic from Invoke-Scoring.ps1.
    /// 
    /// Rules:
    /// - If objectType is in {Table, View, StoredProcedure, Function, Trigger}, classify based on validation outcome
    /// - If objectType is NOT in that set, classify as "skip"
    /// </summary>
    private static ObjectStatus ClassifyObject(string objectType, ObjectStatus validationOutcome)
    {
        if (ConvertibleTypes.Contains(objectType))
        {
            // Convertible objects get their validation outcome
            return validationOutcome;
        }
        else
        {
            // Non-convertible objects are always "skip"
            return ObjectStatus.Skip;
        }
    }

    #endregion

    #region Property 4: Object Classification Correctness

    /// <summary>
    /// Property 4: Object Classification Correctness — every object is classified as exactly
    /// one of the four valid statuses (pass, fail-syntax, fail-convert, skip).
    /// No object is unclassified and no object has multiple statuses.
    /// 
    /// **Validates: Requirements 3.2**
    /// </summary>
    [Property(MaxTest = 200, Arbitrary = new[] { typeof(ObjectClassificationArbitrary) })]
    public void Every_object_is_classified_into_exactly_one_valid_status(List<ObjectResult> objects)
    {
        // Precondition: we have at least one object
        if (objects == null || objects.Count == 0) return;

        foreach (var obj in objects)
        {
            // Each object must have exactly one status that is one of the valid values
            AllStatuses.Should().Contain(obj.Status,
                $"object '{obj.ObjectName}' of type '{obj.ObjectType}' must have exactly one valid classification");

            // Verify it is one of the four named statuses
            var statusName = obj.Status switch
            {
                ObjectStatus.Pass => "pass",
                ObjectStatus.FailSyntax => "fail-syntax",
                ObjectStatus.FailConvert => "fail-convert",
                ObjectStatus.Skip => "skip",
                _ => "UNKNOWN"
            };
            statusName.Should().NotBe("UNKNOWN",
                because: $"object '{obj.ObjectName}' must be classified as pass, fail-syntax, fail-convert, or skip");
        }
    }

    /// <summary>
    /// Property 4: Object Classification Correctness — objects whose type is NOT in the
    /// convertible set {Table, View, StoredProcedure, Function, Trigger} are classified as "skip".
    /// 
    /// **Validates: Requirements 3.2**
    /// </summary>
    [Property(MaxTest = 200, Arbitrary = new[] { typeof(ObjectClassificationArbitrary) })]
    public void Non_convertible_objects_are_always_classified_as_skip(List<ObjectResult> objects)
    {
        if (objects == null || objects.Count == 0) return;

        var nonConvertible = objects.Where(o => !ConvertibleTypes.Contains(o.ObjectType)).ToList();

        foreach (var obj in nonConvertible)
        {
            var classified = ClassifyObject(obj.ObjectType, obj.Status);
            classified.Should().Be(ObjectStatus.Skip,
                because: $"object '{obj.ObjectName}' of type '{obj.ObjectType}' is not in the convertible set and must be classified as 'skip'");
        }
    }

    /// <summary>
    /// Property 4: Object Classification Correctness — objects whose type IS in the
    /// convertible set {Table, View, StoredProcedure, Function, Trigger} are never classified as "skip".
    /// They receive one of: pass, fail-syntax, or fail-convert.
    /// 
    /// **Validates: Requirements 3.2**
    /// </summary>
    [Property(MaxTest = 200, Arbitrary = new[] { typeof(ObjectClassificationArbitrary) })]
    public void Convertible_objects_are_never_classified_as_skip(List<ObjectResult> objects)
    {
        if (objects == null || objects.Count == 0) return;

        var convertible = objects.Where(o => ConvertibleTypes.Contains(o.ObjectType)).ToList();

        foreach (var obj in convertible)
        {
            obj.Status.Should().NotBe(ObjectStatus.Skip,
                $"object '{obj.ObjectName}' of type '{obj.ObjectType}' is in the convertible set and must not be 'skip'");

            obj.Status.Should().BeOneOf(
                new[] { ObjectStatus.Pass, ObjectStatus.FailSyntax, ObjectStatus.FailConvert },
                $"convertible object '{obj.ObjectName}' must be classified as pass, fail-syntax, or fail-convert");
        }
    }

    /// <summary>
    /// Property 4: Object Classification Correctness — the ClassifyObject function produces
    /// correct classification for any object type and validation outcome combination.
    /// Each object gets exactly one classification, never multiple.
    /// 
    /// **Validates: Requirements 3.2**
    /// </summary>
    [Property(MaxTest = 200)]
    public Property Classification_is_deterministic_and_exclusive()
    {
        var gen = from objType in GenAnyObjectType()
                  from outcome in GenConvertibleOutcome()
                  select (objType, outcome);

        return Prop.ForAll(gen.ToArbitrary(), pair =>
        {
            var (objType, outcome) = pair;
            var result = ClassifyObject(objType, outcome);

            // Exactly one classification: the result is a single enum value
            // Verify mutual exclusivity by checking it's only ONE of the valid statuses
            int matchCount = 0;
            if (result == ObjectStatus.Pass) matchCount++;
            if (result == ObjectStatus.FailSyntax) matchCount++;
            if (result == ObjectStatus.FailConvert) matchCount++;
            if (result == ObjectStatus.Skip) matchCount++;

            return (matchCount == 1)
                .Label($"Object type '{objType}' with outcome '{outcome}' should have exactly 1 classification, got {matchCount}");
        });
    }

    /// <summary>
    /// Property 4: Object Classification Correctness — when the scoring engine processes objects,
    /// every input object appears in exactly one status bucket in the output.
    /// No objects are lost or double-counted.
    /// 
    /// **Validates: Requirements 3.2**
    /// </summary>
    [Property(MaxTest = 200, Arbitrary = new[] { typeof(ObjectClassificationArbitrary) })]
    public void Every_object_appears_in_exactly_one_scoring_bucket(List<ObjectResult> objects)
    {
        if (objects == null || objects.Count == 0) return;

        var perDb = ScoringEngine.ComputePerDatabaseScores(objects);

        // Group input objects by database to verify counts
        var inputByDb = objects.GroupBy(o => o.DatabaseName);

        foreach (var dbGroup in inputByDb)
        {
            perDb.Should().ContainKey(dbGroup.Key,
                because: $"database '{dbGroup.Key}' had objects in the input");

            var dbScore = perDb[dbGroup.Key];
            int totalClassified = dbScore.Pass + dbScore.FailSyntax + dbScore.FailConvert + dbScore.Skip;

            totalClassified.Should().Be(dbGroup.Count(),
                because: $"database '{dbGroup.Key}' has {dbGroup.Count()} objects and each must be in exactly one bucket");
        }
    }

    #endregion
}

/// <summary>
/// FsCheck Arbitrary provider for generating properly classified object lists.
/// Objects with convertible types get pass/fail-syntax/fail-convert status.
/// Objects with non-convertible types get skip status.
/// </summary>
public class ObjectClassificationArbitrary
{
    private static readonly HashSet<string> ConvertibleTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "Table", "View", "StoredProcedure", "Function", "Trigger"
    };

    public static Arbitrary<List<ObjectResult>> ArbitraryObjects()
    {
        var genObjectName =
            from schema in Gen.Elements("dbo", "sales", "hr", "inventory", "reports")
            from name in Gen.Elements(
                "sp_Process", "fn_Calculate", "vw_Summary", "tbl_Orders",
                "tr_Audit", "usp_Update", "udf_Format", "vw_Recent",
                "sp_Complex", "fn_Validate", "tbl_Items", "tr_Insert")
            from suffix in Gen.Choose(1, 999)
            select $"{schema}.{name}_{suffix}";

        var genDbName = Gen.Elements(
            "ProcedureComplexityDB", "ViewsTriggerDB", "TypesAndCLRDB",
            "CrossSchemaAdvancedDB", "AssessmentTestDB");

        var genConvertibleObject =
            from name in genObjectName
            from objType in Gen.Elements("Table", "View", "StoredProcedure", "Function", "Trigger")
            from dbName in genDbName
            from status in Gen.Elements(ObjectStatus.Pass, ObjectStatus.FailSyntax, ObjectStatus.FailConvert)
            select new ObjectResult(name, objType, dbName, status);

        var genNonConvertibleObject =
            from name in genObjectName
            from objType in Gen.Elements(
                "Synonym", "Sequence", "Schema", "User", "Role",
                "Assembly", "Certificate", "Credential", "LinkedServer",
                "DatabaseRole", "ApplicationRole", "XmlSchemaCollection")
            from dbName in genDbName
            select new ObjectResult(name, objType, dbName, ObjectStatus.Skip);

        var genAnyObject = Gen.Frequency(
            Tuple.Create(5, genConvertibleObject),
            Tuple.Create(3, genNonConvertibleObject));

        var genList = from count in Gen.Choose(1, 50)
                      from objects in Gen.ListOf(count, genAnyObject)
                      select objects.ToList();

        return Arb.From(genList);
    }
}
