namespace MigrationAssessment.Pipeline.PropertyTests;

/// <summary>
/// Represents a failed object that enters the diagnostics classification pipeline.
/// Mirrors the input structure expected by Invoke-DiagnosticsClassification.ps1.
/// </summary>
public record FailedObject(
    string ObjectName,
    string ObjectType,
    string Status,        // "fail-syntax" or "fail-convert"
    string ErrorMessage,
    int ErrorLineNumber,
    string GeneratedDdl
);

/// <summary>
/// Represents a single failure detail in the diagnostics output.
/// </summary>
public record FailureDetail(
    string ErrorMessage,
    int? LineNumber,
    string Ddl
);

/// <summary>
/// Represents a root cause category with its classified failures.
/// </summary>
public record DiagnosticsCategory(
    string Category,
    int Count,
    List<string> Objects,
    List<FailureDetail> Details
);

/// <summary>
/// C# implementation mirroring Invoke-DiagnosticsClassification.ps1 logic.
/// Classifies failed objects into root cause categories and ensures every
/// failure retains its error message, line number, and DDL in the output.
/// </summary>
public static class DiagnosticsClassifier
{
    /// <summary>
    /// Valid failure statuses that are processed by the diagnostics classifier.
    /// </summary>
    public static readonly string[] FailureStatuses = { "fail-syntax", "fail-convert" };

    /// <summary>
    /// Classifies failed objects into root cause categories.
    /// Every failed object is placed into exactly one category, preserving
    /// its errorMessage, lineNumber, and generatedDdl in the details.
    /// 
    /// Returns categories sorted by count descending (empty categories excluded).
    /// </summary>
    public static List<DiagnosticsCategory> Classify(IEnumerable<FailedObject> failedObjects)
    {
        var categories = new Dictionary<string, (List<string> Objects, List<FailureDetail> Details)>
        {
            ["AI prompt deficiency"] = (new List<string>(), new List<FailureDetail>()),
            ["type mapping gap"] = (new List<string>(), new List<FailureDetail>()),
            ["function mapping gap"] = (new List<string>(), new List<FailureDetail>()),
            ["procedural pattern not handled"] = (new List<string>(), new List<FailureDetail>()),
            ["dependency resolution failure"] = (new List<string>(), new List<FailureDetail>()),
        };

        foreach (var obj in failedObjects)
        {
            string errorMsg = obj.ErrorMessage ?? "";
            string ddl = obj.GeneratedDdl ?? "";
            string objectName = obj.ObjectName ?? "unknown";
            int? lineNumber = obj.ErrorLineNumber;

            string matchedCategory = ClassifySingleObject(errorMsg, ddl);

            categories[matchedCategory].Objects.Add(objectName);
            categories[matchedCategory].Details.Add(new FailureDetail(errorMsg, lineNumber, ddl));
        }

        return categories
            .Where(kvp => kvp.Value.Objects.Count > 0)
            .OrderByDescending(kvp => kvp.Value.Objects.Count)
            .Select(kvp => new DiagnosticsCategory(
                kvp.Key,
                kvp.Value.Objects.Count,
                kvp.Value.Objects,
                kvp.Value.Details))
            .ToList();
    }

    /// <summary>
    /// Classifies a single failed object into one root cause category.
    /// Mirrors the pattern-matching logic in Invoke-DiagnosticsClassification.ps1.
    /// </summary>
    private static string ClassifySingleObject(string errorMessage, string ddl)
    {
        // AI prompt deficiency: empty/placeholder DDL or error indicates empty output
        if (IsAiPromptDeficiency(errorMessage, ddl))
            return "AI prompt deficiency";

        // Type mapping gap: error references unrecognized data type
        if (IsTypeMappingGap(errorMessage))
            return "type mapping gap";

        // Function mapping gap: error references undefined function or operator
        if (IsFunctionMappingGap(errorMessage))
            return "function mapping gap";

        // Procedural pattern not handled: error within PL/pgSQL block
        if (IsProceduralPatternNotHandled(errorMessage, ddl))
            return "procedural pattern not handled";

        // Dependency resolution failure: error references missing object
        if (IsDependencyResolutionFailure(errorMessage))
            return "dependency resolution failure";

        // Default: procedural pattern not handled (catch-all per PS1 script)
        return "procedural pattern not handled";
    }

    private static bool IsAiPromptDeficiency(string errorMessage, string ddl)
    {
        // DDL is empty or contains only whitespace/comments/placeholders
        if (string.IsNullOrWhiteSpace(ddl) ||
            System.Text.RegularExpressions.Regex.IsMatch(ddl, @"(?i)^(\s*(--|/\*.*\*/)\s*)*$") ||
            System.Text.RegularExpressions.Regex.IsMatch(ddl, @"(?i)TODO|PLACEHOLDER|NOT_IMPLEMENTED"))
            return true;

        // Error message indicates empty/placeholder output
        if (System.Text.RegularExpressions.Regex.IsMatch(errorMessage,
            @"(?i)(empty|placeholder|todo|not implemented|stub|no output|null|blank)\s*(output|result|conversion|body|content)?"))
        {
            if (string.IsNullOrWhiteSpace(ddl))
                return true;
        }

        return false;
    }

    private static bool IsTypeMappingGap(string errorMessage)
    {
        return System.Text.RegularExpressions.Regex.IsMatch(errorMessage,
            @"(?i)(type\s+""?[\w.]+""?\s+(does not exist|is not defined|unknown|undefined))|(unrecognized\s+data\s*type)|(cannot\s+cast.*type)|(column\s+""?\w+""?\s+.*type\s+""?[\w.]+""?\s+(does not exist|unknown))");
    }

    private static bool IsFunctionMappingGap(string errorMessage)
    {
        return System.Text.RegularExpressions.Regex.IsMatch(errorMessage,
            @"(?i)(function\s+""?[\w.]+""?\s*(\(.*\)\s+)?does not exist)|(operator\s+(does not exist|is not unique))|(undefined\s+function)|(unknown\s+function)|(no\s+function\s+matches)");
    }

    private static bool IsProceduralPatternNotHandled(string errorMessage, string ddl)
    {
        bool errorMatch = System.Text.RegularExpressions.Regex.IsMatch(errorMessage,
            @"(?i)(syntax\s+error.*(BEGIN|END|DECLARE|LOOP|IF|ELSE|RETURN|RAISE|EXCEPTION|EXECUTE|PERFORM))|(at\s+or\s+near\s+""?(BEGIN|END|DECLARE|LOOP|IF|ELSE|RETURN|RAISE|EXCEPTION)""?)|(ERROR.*PL/pgSQL)|(unterminated\s+(block|function|procedure))");

        if (!errorMatch) return false;

        bool ddlMatch = System.Text.RegularExpressions.Regex.IsMatch(ddl,
            @"(?i)(CREATE\s+(OR\s+REPLACE\s+)?(FUNCTION|PROCEDURE)\b)|(DO\s*\$\$)|\$\$\s*LANGUAGE\s+plpgsql|(BEGIN\s)");

        // Match if DDL matches procedural pattern, or error references PL/pgSQL directly
        return ddlMatch ||
               System.Text.RegularExpressions.Regex.IsMatch(errorMessage, @"(?i)PL/pgSQL|plpgsql") ||
               errorMatch;
    }

    private static bool IsDependencyResolutionFailure(string errorMessage)
    {
        return System.Text.RegularExpressions.Regex.IsMatch(errorMessage,
            @"(?i)(relation\s+""?[\w.]+""?\s+(does not exist|not found|cannot be found|unknown|undefined|missing))|(table\s+""?[\w.]+""?\s+(does not exist|not found))|(view\s+""?[\w.]+""?\s+(does not exist|not found))|(schema\s+""?[\w.]+""?\s+(does not exist|not found))");
    }
}
