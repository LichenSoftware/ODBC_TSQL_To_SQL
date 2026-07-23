using FluentAssertions;
using FsCheck;
using FsCheck.Xunit;
using System.Text.RegularExpressions;

namespace MigrationAssessment.Pipeline.PropertyTests;

/// <summary>
/// Feature: migration-validation-pipeline, Property 8: Root Cause Classification
/// 
/// Validates: Requirements 4.2, 4.6
/// 
/// For any set of failed objects, each failure SHALL be classified into exactly one root cause
/// category based on pattern matching (type mapping gap, function mapping gap, procedural pattern
/// not handled, AI prompt deficiency, dependency resolution failure), and categories SHALL be
/// ranked in descending order by failure count with affected object names listed per category.
/// </summary>
public class RootCauseClassificationPropertyTests
{
    #region Root Cause Categories

    /// <summary>
    /// All valid root cause category names.
    /// </summary>
    private static readonly string[] AllCategories =
    {
        "AI prompt deficiency",
        "type mapping gap",
        "function mapping gap",
        "procedural pattern not handled",
        "dependency resolution failure"
    };

    #endregion

    #region Classification Engine (mirrors Invoke-DiagnosticsClassification.ps1)

    /// <summary>
    /// Represents a failed object for classification.
    /// </summary>
    public record FailedObject(
        string ObjectName,
        string ObjectType,
        string Status,
        string ErrorMessage,
        int? ErrorLineNumber,
        string GeneratedDdl
    );

    /// <summary>
    /// Represents a classified category result.
    /// </summary>
    public record CategoryResult(
        string Category,
        int Count,
        List<string> Objects
    );

    /// <summary>
    /// Category pattern definitions mirroring the PowerShell implementation.
    /// Order matters: patterns are evaluated top-to-bottom, first match wins.
    /// </summary>
    private static readonly (string Category, string ErrorPattern, string? DdlPattern)[] CategoryPatterns =
    {
        (
            "AI prompt deficiency",
            @"(?i)(empty|placeholder|todo|not implemented|stub|no output|null|blank)\s*(output|result|conversion|body|content)?",
            @"(?i)^(\s*(--|/\*.*\*/)\s*)*$|^\s*$|TODO|PLACEHOLDER|NOT_IMPLEMENTED"
        ),
        (
            "type mapping gap",
            @"(?i)(type\s+""?[\w.]+""?\s+(does not exist|is not defined|unknown|undefined))|(unrecognized\s+data\s*type)|(cannot\s+cast.*type)|(column\s+""?\w+""?\s+.*type\s+""?[\w.]+""?\s+(does not exist|unknown))",
            null
        ),
        (
            "function mapping gap",
            @"(?i)(function\s+""?[\w.]+""?\s*(\(.*\)\s+)?does not exist)|(operator\s+(does not exist|is not unique))|(undefined\s+function)|(unknown\s+function)|(no\s+function\s+matches)",
            null
        ),
        (
            "procedural pattern not handled",
            @"(?i)(syntax\s+error.*(BEGIN|END|DECLARE|LOOP|IF|ELSE|RETURN|RAISE|EXCEPTION|EXECUTE|PERFORM))|(at\s+or\s+near\s+""?(BEGIN|END|DECLARE|LOOP|IF|ELSE|RETURN|RAISE|EXCEPTION)""?)|(ERROR.*PL/pgSQL)|(unterminated\s+(block|function|procedure))",
            @"(?i)(CREATE\s+(OR\s+REPLACE\s+)?(FUNCTION|PROCEDURE)\b)|(DO\s*\$\$)|\$\$\s*LANGUAGE\s+plpgsql|(BEGIN\s)"
        ),
        (
            "dependency resolution failure",
            @"(?i)(relation\s+""?[\w.]+""?\s+(does not exist|not found|cannot be found|unknown|undefined|missing))|(table\s+""?[\w.]+""?\s+(does not exist|not found))|(view\s+""?[\w.]+""?\s+(does not exist|not found))|(schema\s+""?[\w.]+""?\s+(does not exist|not found))",
            null
        )
    };

    /// <summary>
    /// Classifies a list of failed objects into root cause categories.
    /// Mirrors the PowerShell Invoke-DiagnosticsClassification logic exactly.
    /// Returns categories sorted by count descending with affected objects listed.
    /// </summary>
    public static List<CategoryResult> ClassifyFailures(IEnumerable<FailedObject> failedObjects)
    {
        var categories = new Dictionary<string, List<string>>();
        foreach (var cat in AllCategories)
        {
            categories[cat] = new List<string>();
        }

        foreach (var obj in failedObjects)
        {
            string errorMsg = obj.ErrorMessage ?? "";
            string ddl = obj.GeneratedDdl ?? "";
            string objectName = obj.ObjectName ?? "unknown";

            bool classified = false;

            foreach (var (category, errorPattern, ddlPattern) in CategoryPatterns)
            {
                bool errorMatch = !string.IsNullOrEmpty(errorPattern) &&
                                  Regex.IsMatch(errorMsg, errorPattern);
                bool ddlMatch = !string.IsNullOrEmpty(ddlPattern) &&
                                Regex.IsMatch(ddl, ddlPattern);

                bool isMatch = false;

                switch (category)
                {
                    case "AI prompt deficiency":
                        if (ddlMatch)
                            isMatch = true;
                        else if (errorMatch && string.IsNullOrWhiteSpace(ddl))
                            isMatch = true;
                        break;

                    case "procedural pattern not handled":
                        if (errorMatch)
                            isMatch = true;
                        break;

                    default:
                        if (errorMatch)
                            isMatch = true;
                        break;
                }

                if (isMatch)
                {
                    categories[category].Add(objectName);
                    classified = true;
                    break; // First match wins
                }
            }

            // Default to 'procedural pattern not handled' as catch-all
            if (!classified)
            {
                categories["procedural pattern not handled"].Add(objectName);
            }
        }

        // Filter empty categories, sort by count descending
        return categories
            .Where(kvp => kvp.Value.Count > 0)
            .OrderByDescending(kvp => kvp.Value.Count)
            .Select(kvp => new CategoryResult(kvp.Key, kvp.Value.Count, kvp.Value))
            .ToList();
    }

    #endregion

    #region Generators

    /// <summary>
    /// Generates an error message that matches the "type mapping gap" category.
    /// </summary>
    private static Gen<string> GenTypeMappingError()
    {
        return Gen.Elements(
            "type \"hierarchyid\" does not exist",
            "type \"geography\" does not exist",
            "type \"xml\" is not defined",
            "unrecognized data type: money",
            "cannot cast value to type \"datetimeoffset\"",
            "column \"col1\" has type \"uniqueidentifier\" does not exist",
            "type \"sql_variant\" unknown",
            "type \"image\" does not exist",
            "type \"ntext\" undefined"
        );
    }

    /// <summary>
    /// Generates an error message that matches the "function mapping gap" category.
    /// </summary>
    private static Gen<string> GenFunctionMappingError()
    {
        return Gen.Elements(
            "function \"ISNULL\" does not exist",
            "function \"GETDATE\"() does not exist",
            "operator does not exist: varchar + int",
            "undefined function: CHARINDEX",
            "unknown function PATINDEX",
            "no function matches the given name and argument types",
            "function \"CONVERT\"(varchar, int) does not exist",
            "operator is not unique: text || integer",
            "function \"SCOPE_IDENTITY\" does not exist"
        );
    }

    /// <summary>
    /// Generates an error message that matches the "procedural pattern not handled" category.
    /// </summary>
    private static Gen<string> GenProceduralPatternError()
    {
        return Gen.Elements(
            "syntax error at or near \"DECLARE\"",
            "syntax error at or near \"BEGIN\"",
            "at or near \"RETURN\" syntax error",
            "ERROR in PL/pgSQL function body",
            "unterminated block in function",
            "syntax error at or near \"EXECUTE\"",
            "syntax error near END in LOOP",
            "at or near \"EXCEPTION\" unexpected",
            "unterminated procedure definition"
        );
    }

    /// <summary>
    /// Generates an error message that matches the "AI prompt deficiency" category.
    /// </summary>
    private static Gen<string> GenAiPromptDeficiencyError()
    {
        return Gen.Elements(
            "empty output from conversion",
            "placeholder result returned",
            "TODO: implement conversion",
            "not implemented conversion body",
            "stub output generated",
            "no output produced",
            "null result from conversion",
            "blank content generated"
        );
    }

    /// <summary>
    /// Generates an error message that matches the "dependency resolution failure" category.
    /// Each message must match the regex pattern:
    /// (relation "X" (does not exist|not found|cannot be found|missing)) |
    /// (table "X" (does not exist|not found)) |
    /// (view "X" (does not exist|not found)) |
    /// (schema "X" (does not exist|not found))
    /// </summary>
    private static Gen<string> GenDependencyResolutionError()
    {
        return Gen.Elements(
            "relation \"dbo.Orders\" does not exist",
            "relation \"dbo.Customers\" not found",
            "relation \"hr.Employees\" cannot be found",
            "relation \"sales.Invoices\" missing",
            "table \"inventory.Products\" does not exist",
            "table \"dbo.OrderItems\" not found",
            "view \"dbo.vw_Summary\" does not exist",
            "view \"reports.vw_Monthly\" not found",
            "schema \"sales\" does not exist",
            "schema \"audit\" not found"
        );
    }

    /// <summary>
    /// Generates a DDL string for procedural context (function/procedure).
    /// </summary>
    private static Gen<string> GenProceduralDdl()
    {
        return Gen.Elements(
            "CREATE OR REPLACE FUNCTION dbo.sp_Process() RETURNS void AS $$ BEGIN RAISE NOTICE 'test'; END; $$ LANGUAGE plpgsql;",
            "CREATE OR REPLACE PROCEDURE dbo.sp_Update() LANGUAGE plpgsql AS $$ BEGIN NULL; END; $$;",
            "DO $$ BEGIN PERFORM 1; END; $$;",
            "CREATE FUNCTION test() RETURNS int AS $$ BEGIN RETURN 1; END; $$ LANGUAGE plpgsql;"
        );
    }

    /// <summary>
    /// Generates an empty or placeholder DDL for AI prompt deficiency context.
    /// These MUST match the AI prompt deficiency DDL pattern:
    /// ^(\s*(--|/\*.*\*/)\s*)*$ | ^\s*$ | TODO | PLACEHOLDER | NOT_IMPLEMENTED
    /// OR be whitespace/empty so string.IsNullOrWhiteSpace returns true.
    /// </summary>
    private static Gen<string> GenEmptyOrPlaceholderDdl()
    {
        return Gen.Elements(
            "",
            "   ",
            "TODO: convert this object",
            "PLACEHOLDER for future conversion",
            "NOT_IMPLEMENTED",
            "TODO",
            "PLACEHOLDER"
        );
    }

    /// <summary>
    /// Generates a non-empty DDL for non-AI-deficiency objects.
    /// </summary>
    private static Gen<string> GenNonEmptyDdl()
    {
        return Gen.Elements(
            "CREATE TABLE dbo.Orders (id int PRIMARY KEY, name text);",
            "CREATE OR REPLACE FUNCTION dbo.fn_Calc() RETURNS int AS $$ BEGIN RETURN 42; END; $$ LANGUAGE plpgsql;",
            "CREATE VIEW dbo.vw_Active AS SELECT * FROM orders WHERE active = true;",
            "CREATE OR REPLACE PROCEDURE dbo.sp_Run() LANGUAGE plpgsql AS $$ BEGIN NULL; END; $$;",
            "CREATE TABLE hr.Employees (emp_id serial PRIMARY KEY, name varchar(100));"
        );
    }

    /// <summary>
    /// Generates a random object name.
    /// </summary>
    private static Gen<string> GenObjectName()
    {
        return from schema in Gen.Elements("dbo", "sales", "hr", "inventory", "reports")
               from name in Gen.Elements(
                   "sp_ProcessOrder", "fn_CalcTotal", "vw_Summary", "tbl_Items",
                   "tr_AuditLog", "sp_UpdateStock", "fn_FormatDate", "vw_Recent",
                   "sp_ComplexCursor", "fn_Validate", "tbl_Products", "tr_Insert",
                   "sp_Report", "vw_Monthly", "fn_Convert", "tbl_Users")
               from suffix in Gen.Choose(1, 500)
               select $"{schema}.{name}_{suffix}";
    }

    /// <summary>
    /// Generates a random object type for failed objects.
    /// </summary>
    private static Gen<string> GenObjectType()
    {
        return Gen.Elements("StoredProcedure", "Function", "View", "Table", "Trigger");
    }

    /// <summary>
    /// Generates a failed object that matches a specific category.
    /// </summary>
    private static Gen<FailedObject> GenFailedObjectForCategory(string category)
    {
        return category switch
        {
            "type mapping gap" =>
                from name in GenObjectName()
                from objType in GenObjectType()
                from error in GenTypeMappingError()
                from ddl in GenNonEmptyDdl()
                from line in Gen.Choose(1, 100)
                select new FailedObject(name, objType, "fail-syntax", error, line, ddl),

            "function mapping gap" =>
                from name in GenObjectName()
                from objType in GenObjectType()
                from error in GenFunctionMappingError()
                from ddl in GenNonEmptyDdl()
                from line in Gen.Choose(1, 100)
                select new FailedObject(name, objType, "fail-syntax", error, line, ddl),

            "procedural pattern not handled" =>
                from name in GenObjectName()
                from objType in Gen.Elements("StoredProcedure", "Function")
                from error in GenProceduralPatternError()
                from ddl in GenProceduralDdl()
                from line in Gen.Choose(1, 100)
                select new FailedObject(name, objType, "fail-syntax", error, line, ddl),

            "AI prompt deficiency" =>
                from name in GenObjectName()
                from objType in GenObjectType()
                from error in GenAiPromptDeficiencyError()
                from ddl in GenEmptyOrPlaceholderDdl()
                from line in Gen.Choose(1, 100)
                select new FailedObject(name, objType, "fail-convert", error, line, ddl),

            "dependency resolution failure" =>
                from name in GenObjectName()
                from objType in GenObjectType()
                from error in GenDependencyResolutionError()
                from ddl in GenNonEmptyDdl()
                from line in Gen.Choose(1, 100)
                select new FailedObject(name, objType, "fail-syntax", error, line, ddl),

            _ => throw new ArgumentException($"Unknown category: {category}")
        };
    }

    /// <summary>
    /// Generates a mixed list of failed objects across all categories.
    /// </summary>
    private static Gen<List<FailedObject>> GenMixedFailedObjects()
    {
        return from typeMappingCount in Gen.Choose(0, 8)
               from functionMappingCount in Gen.Choose(0, 8)
               from proceduralCount in Gen.Choose(0, 8)
               from aiPromptCount in Gen.Choose(0, 8)
               from dependencyCount in Gen.Choose(0, 8)
               let totalCount = typeMappingCount + functionMappingCount + proceduralCount + aiPromptCount + dependencyCount
               where totalCount >= 1
               from typeMapping in Gen.ListOf(typeMappingCount, GenFailedObjectForCategory("type mapping gap"))
               from functionMapping in Gen.ListOf(functionMappingCount, GenFailedObjectForCategory("function mapping gap"))
               from procedural in Gen.ListOf(proceduralCount, GenFailedObjectForCategory("procedural pattern not handled"))
               from aiPrompt in Gen.ListOf(aiPromptCount, GenFailedObjectForCategory("AI prompt deficiency"))
               from dependency in Gen.ListOf(dependencyCount, GenFailedObjectForCategory("dependency resolution failure"))
               select typeMapping.Concat(functionMapping).Concat(procedural).Concat(aiPrompt).Concat(dependency).ToList();
    }

    /// <summary>
    /// Generates a list of failed objects that all belong to a single category.
    /// </summary>
    private static Gen<(string Category, List<FailedObject> Objects)> GenSingleCategoryObjects()
    {
        return from category in Gen.Elements(AllCategories)
               from count in Gen.Choose(1, 10)
               from objects in Gen.ListOf(count, GenFailedObjectForCategory(category))
               select (category, objects.ToList());
    }

    #endregion

    #region Property 8: Root Cause Classification

    /// <summary>
    /// Property 8: Root Cause Classification — each failure is classified into exactly ONE
    /// category (no failure is in 0 or 2+ categories).
    /// 
    /// **Validates: Requirements 4.2, 4.6**
    /// </summary>
    [Property(MaxTest = 200)]
    public Property Each_failure_is_classified_into_exactly_one_category()
    {
        return Prop.ForAll(GenMixedFailedObjects().ToArbitrary(), failedObjects =>
        {
            var results = ClassifyFailures(failedObjects);

            // Sum of all category counts must equal total number of failed objects
            int totalClassified = results.Sum(r => r.Count);
            totalClassified.Should().Be(failedObjects.Count,
                "every failed object must be classified into exactly one category");

            // Each object name should appear in exactly one category
            var allClassifiedNames = results.SelectMany(r => r.Objects).ToList();
            allClassifiedNames.Count.Should().Be(failedObjects.Count,
                "every failed object should appear once across all categories");

            return true.ToProperty();
        });
    }

    /// <summary>
    /// Property 8: Root Cause Classification — categories are ranked by failure count 
    /// in descending order.
    /// 
    /// **Validates: Requirements 4.2, 4.6**
    /// </summary>
    [Property(MaxTest = 200)]
    public Property Categories_are_ranked_by_failure_count_descending()
    {
        return Prop.ForAll(GenMixedFailedObjects().ToArbitrary(), failedObjects =>
        {
            var results = ClassifyFailures(failedObjects);

            if (results.Count <= 1) return true.ToProperty();

            // Verify descending order
            for (int i = 0; i < results.Count - 1; i++)
            {
                results[i].Count.Should().BeGreaterThanOrEqualTo(results[i + 1].Count,
                    $"category '{results[i].Category}' (count={results[i].Count}) should have >= count " +
                    $"than '{results[i + 1].Category}' (count={results[i + 1].Count})");
            }

            return true.ToProperty();
        });
    }

    /// <summary>
    /// Property 8: Root Cause Classification — the sum of all category counts equals
    /// the total number of failed objects.
    /// 
    /// **Validates: Requirements 4.2, 4.6**
    /// </summary>
    [Property(MaxTest = 200)]
    public Property Sum_of_category_counts_equals_total_failed_objects()
    {
        return Prop.ForAll(GenMixedFailedObjects().ToArbitrary(), failedObjects =>
        {
            var results = ClassifyFailures(failedObjects);

            int totalInCategories = results.Sum(r => r.Count);

            return (totalInCategories == failedObjects.Count)
                .Label($"Total in categories ({totalInCategories}) should equal input count ({failedObjects.Count})");
        });
    }

    /// <summary>
    /// Property 8: Root Cause Classification — every category in the result is one of the
    /// 5 defined root cause categories.
    /// 
    /// **Validates: Requirements 4.2, 4.6**
    /// </summary>
    [Property(MaxTest = 200)]
    public Property All_categories_in_result_are_valid_defined_categories()
    {
        return Prop.ForAll(GenMixedFailedObjects().ToArbitrary(), failedObjects =>
        {
            var results = ClassifyFailures(failedObjects);

            foreach (var result in results)
            {
                AllCategories.Should().Contain(result.Category,
                    $"category '{result.Category}' must be one of the 5 defined categories");
            }

            return true.ToProperty();
        });
    }

    /// <summary>
    /// Property 8: Root Cause Classification — objects generated for a specific category
    /// are correctly classified into that category.
    /// 
    /// **Validates: Requirements 4.2, 4.6**
    /// </summary>
    [Property(MaxTest = 200)]
    public Property Objects_with_category_patterns_are_classified_correctly()
    {
        return Prop.ForAll(GenSingleCategoryObjects().ToArbitrary(), input =>
        {
            var (expectedCategory, objects) = input;
            var results = ClassifyFailures(objects);

            // All objects should be classified
            int totalClassified = results.Sum(r => r.Count);
            totalClassified.Should().Be(objects.Count,
                $"all {objects.Count} objects should be classified");

            // The expected category should be the primary (or only) category
            var matchingCategory = results.FirstOrDefault(r => r.Category == expectedCategory);
            matchingCategory.Should().NotBeNull(
                $"objects generated for '{expectedCategory}' should appear in that category");

            matchingCategory!.Count.Should().Be(objects.Count,
                $"all {objects.Count} objects generated for '{expectedCategory}' should be in that category");

            return true.ToProperty();
        });
    }

    /// <summary>
    /// Property 8: Root Cause Classification — affected object names are listed per category
    /// and each name corresponds to an input failed object.
    /// 
    /// **Validates: Requirements 4.2, 4.6**
    /// </summary>
    [Property(MaxTest = 200)]
    public Property Affected_object_names_are_listed_per_category()
    {
        return Prop.ForAll(GenMixedFailedObjects().ToArbitrary(), failedObjects =>
        {
            var results = ClassifyFailures(failedObjects);

            var inputNames = failedObjects.Select(o => o.ObjectName).ToList();

            foreach (var result in results)
            {
                // Each object in the result must be from the input
                foreach (var objName in result.Objects)
                {
                    inputNames.Should().Contain(objName,
                        $"object '{objName}' in category '{result.Category}' must be from the input");
                }

                // Count must match objects list length
                result.Count.Should().Be(result.Objects.Count,
                    $"category '{result.Category}' count ({result.Count}) must equal objects list length ({result.Objects.Count})");
            }

            return true.ToProperty();
        });
    }

    /// <summary>
    /// Property 8: Root Cause Classification — empty input produces empty output.
    /// 
    /// **Validates: Requirements 4.2, 4.6**
    /// </summary>
    [Fact]
    public void Empty_input_produces_no_categories()
    {
        var results = ClassifyFailures(Array.Empty<FailedObject>());
        results.Should().BeEmpty("no failures means no categories");
    }

    #endregion
}
