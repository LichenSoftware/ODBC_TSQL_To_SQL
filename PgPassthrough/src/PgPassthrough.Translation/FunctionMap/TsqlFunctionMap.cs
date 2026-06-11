namespace PgPassthrough.Translation.FunctionMap;

/// <summary>
/// Maps T-SQL function names to their PostgreSQL equivalents.
/// 
/// Three categories:
///   1. Direct rename: same semantics, different name (GETDATE → NOW)
///   2. Signature change: same concept, different argument order or structure
///   3. Unsupported: no PostgreSQL equivalent (emit warning)
///
/// This map covers the ~50 most common T-SQL functions seen in OLTP applications.
/// </summary>
internal static class TsqlFunctionMap
{
    /// <summary>
    /// Simple 1:1 renames. Key = T-SQL name (UPPER), Value = PostgreSQL name.
    /// </summary>
    public static readonly Dictionary<string, string> DirectRenames = new(StringComparer.OrdinalIgnoreCase)
    {
        // Date/time
        ["GETDATE"]          = "NOW",
        ["GETUTCDATE"]       = "NOW() AT TIME ZONE 'UTC'",  // special: no parens on the PG side
        ["SYSDATETIME"]      = "NOW",
        ["SYSUTCDATETIME"]   = "NOW() AT TIME ZONE 'UTC'",
        ["CURRENT_TIMESTAMP"]= "NOW",

        // String
        ["LEN"]              = "LENGTH",
        ["DATALENGTH"]       = "OCTET_LENGTH",
        ["CHARINDEX"]        = "POSITION",  // special: different arg order handled below
        ["REPLICATE"]        = "REPEAT",
        ["STUFF"]            = "OVERLAY",   // special: different syntax handled below
        ["SPACE"]            = "REPEAT",    // special: SPACE(n) → REPEAT(' ', n)
        ["RTRIM"]            = "RTRIM",
        ["LTRIM"]            = "LTRIM",
        ["UPPER"]            = "UPPER",
        ["LOWER"]            = "LOWER",
        ["REPLACE"]          = "REPLACE",
        ["REVERSE"]          = "REVERSE",
        ["LEFT"]             = "LEFT",
        ["RIGHT"]            = "RIGHT",
        ["TRIM"]             = "TRIM",
        ["STRING_AGG"]       = "STRING_AGG",
        ["CONCAT"]           = "CONCAT",
        ["CONCAT_WS"]        = "CONCAT_WS",
        ["FORMAT"]           = "TO_CHAR",   // approximate — FORMAT is more complex

        // Math
        ["ABS"]              = "ABS",
        ["CEILING"]          = "CEIL",
        ["FLOOR"]            = "FLOOR",
        ["ROUND"]            = "ROUND",
        ["POWER"]            = "POWER",
        ["SQRT"]             = "SQRT",
        ["SIGN"]             = "SIGN",
        ["LOG"]              = "LN",
        ["LOG10"]            = "LOG",
        ["RAND"]             = "RANDOM",
        ["PI"]               = "PI",
        ["SQUARE"]           = "POWER",     // SQUARE(x) → POWER(x, 2)

        // Aggregate
        ["COUNT"]            = "COUNT",
        ["SUM"]              = "SUM",
        ["AVG"]              = "AVG",
        ["MIN"]              = "MIN",
        ["MAX"]              = "MAX",
        ["COUNT_BIG"]        = "COUNT",

        // Conversion
        ["NEWID"]            = "GEN_RANDOM_UUID",

        // Type checking
        ["ISNUMERIC"]        = "ISNUMERIC", // needs custom function in PG
        ["ISDATE"]           = "ISDATE",    // needs custom function in PG

        // Window
        ["ROW_NUMBER"]       = "ROW_NUMBER",
        ["RANK"]             = "RANK",
        ["DENSE_RANK"]       = "DENSE_RANK",
        ["NTILE"]            = "NTILE",
        ["LAG"]              = "LAG",
        ["LEAD"]             = "LEAD",
        ["FIRST_VALUE"]      = "FIRST_VALUE",
        ["LAST_VALUE"]       = "LAST_VALUE",
    };

    /// <summary>
    /// Functions that require structural transformation (not just a rename).
    /// The translator handles these in dedicated code paths.
    /// </summary>
    public static readonly HashSet<string> SpecialFunctions = new(StringComparer.OrdinalIgnoreCase)
    {
        "ISNULL",           // ISNULL(a,b) → COALESCE(a,b)
        "CHARINDEX",        // CHARINDEX(sub, str) → POSITION(sub IN str)
        "SUBSTRING",        // SUBSTRING(s, start, len) → SUBSTRING(s FROM start FOR len)
        "STUFF",            // STUFF(s, start, len, rep) → OVERLAY(s PLACING rep FROM start FOR len)
        "DATEADD",          // DATEADD(part, n, date) → date + INTERVAL 'n part'
        "DATEDIFF",         // DATEDIFF(part, start, end) → EXTRACT/DATE_PART based
        "DATEPART",         // DATEPART(part, date) → EXTRACT(part FROM date)
        "DATENAME",         // DATENAME(part, date) → TO_CHAR(date, format)
        "CONVERT",          // CONVERT(type, val, style) → CAST or TO_CHAR
        "SPACE",            // SPACE(n) → REPEAT(' ', n)
        "SQUARE",           // SQUARE(x) → POWER(x, 2)
        "PATINDEX",         // PATINDEX(pat, str) → requires regex
        "OBJECT_ID",        // OBJECT_ID('name') → to_regclass equivalent
        "SCOPE_IDENTITY",   // → lastval() or CURRVAL
        "IDENT_CURRENT",    // → CURRVAL of sequence
    };

    /// <summary>
    /// Global variables and their PostgreSQL translations.
    /// </summary>
    public static readonly Dictionary<string, string> GlobalVariables = new(StringComparer.OrdinalIgnoreCase)
    {
        ["@@ROWCOUNT"]       = "pg_catalog.lastval()",  // approximation — needs session tracking
        ["@@IDENTITY"]       = "lastval()",
        ["@@ERROR"]          = "0",                     // PG uses exceptions, not @@ERROR
        ["@@TRANCOUNT"]      = "CASE WHEN pg_current_xact_id_if_assigned() IS NOT NULL THEN 1 ELSE 0 END",
        ["@@VERSION"]        = "version()",
        ["@@SERVERNAME"]     = "current_setting('cluster_name')",
        ["@@SPID"]           = "pg_backend_pid()",
        ["@@FETCH_STATUS"]   = "0",                     // cursor-specific, simplified
    };
}
