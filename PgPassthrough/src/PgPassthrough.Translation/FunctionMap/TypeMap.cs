namespace PgPassthrough.Translation.FunctionMap;

/// <summary>
/// Maps T-SQL data type names to PostgreSQL equivalents.
/// Used in CAST/CONVERT expressions and CREATE TABLE DDL.
/// </summary>
internal static class TypeMap
{
    /// <summary>
    /// T-SQL type name (UPPER) → PostgreSQL type name.
    /// Types with parameters (length, precision, scale) are handled
    /// by the caller — this map only covers the base type name.
    /// </summary>
    public static readonly Dictionary<string, string> Map = new(StringComparer.OrdinalIgnoreCase)
    {
        // Exact numerics
        ["INT"]              = "INTEGER",
        ["INTEGER"]          = "INTEGER",
        ["BIGINT"]           = "BIGINT",
        ["SMALLINT"]         = "SMALLINT",
        ["TINYINT"]          = "SMALLINT",       // PG has no TINYINT; SMALLINT is the closest
        ["BIT"]              = "BOOLEAN",
        ["DECIMAL"]          = "NUMERIC",
        ["NUMERIC"]          = "NUMERIC",
        ["MONEY"]            = "NUMERIC(19,4)",   // PG MONEY type has locale issues; NUMERIC is safer
        ["SMALLMONEY"]       = "NUMERIC(10,4)",

        // Approximate numerics
        ["FLOAT"]            = "DOUBLE PRECISION",
        ["REAL"]             = "REAL",

        // Character strings
        ["CHAR"]             = "CHAR",
        ["CHARACTER"]        = "CHAR",
        ["VARCHAR"]          = "VARCHAR",
        ["TEXT"]             = "TEXT",
        ["NCHAR"]            = "CHAR",           // PG uses UTF-8 natively; N-prefix is irrelevant
        ["NVARCHAR"]         = "VARCHAR",
        ["NTEXT"]            = "TEXT",

        // Binary
        ["BINARY"]           = "BYTEA",
        ["VARBINARY"]        = "BYTEA",
        ["IMAGE"]            = "BYTEA",

        // Date/time
        ["DATETIME"]         = "TIMESTAMP",
        ["DATETIME2"]        = "TIMESTAMP",
        ["SMALLDATETIME"]    = "TIMESTAMP(0)",
        ["DATE"]             = "DATE",
        ["TIME"]             = "TIME",
        ["DATETIMEOFFSET"]   = "TIMESTAMPTZ",
        ["TIMESTAMP"]        = "BYTEA",          // SQL Server TIMESTAMP is actually ROWVERSION (8 bytes)

        // Other
        ["UNIQUEIDENTIFIER"] = "UUID",
        ["XML"]              = "XML",
        ["SQL_VARIANT"]      = "TEXT",           // No direct PG equivalent
        ["HIERARCHYID"]      = "TEXT",
        ["GEOGRAPHY"]        = "GEOGRAPHY",      // Requires PostGIS
        ["GEOMETRY"]         = "GEOMETRY",       // Requires PostGIS
        ["ROWVERSION"]       = "BYTEA",
        ["SYSNAME"]          = "VARCHAR(128)",
    };

    /// <summary>
    /// Translates a T-SQL type name with optional length/precision/scale
    /// to its PostgreSQL equivalent.
    /// </summary>
    public static string Translate(string typeName, int? length, bool isMax, int? precision, int? scale)
    {
        string upper = typeName.ToUpperInvariant();

        // Handle MAX types
        if (isMax && (upper == "VARCHAR" || upper == "NVARCHAR" || upper == "VARBINARY"))
        {
            return upper == "VARBINARY" ? "BYTEA" : "TEXT";
        }

        if (!Map.TryGetValue(upper, out string? pgType))
            pgType = upper; // pass through unknown types unchanged

        // Handle precision/scale
        if (precision != null)
        {
            return scale != null
                ? $"{pgType}({precision},{scale})"
                : $"{pgType}({precision})";
        }

        // Handle length — some types don't take a length parameter in PG
        if (length != null)
        {
            // BYTEA, TEXT, etc. don't use length in PG
            if (pgType is "BYTEA" or "TEXT" or "BOOLEAN" or "UUID" or "XML")
                return pgType;

            // CHAR/VARCHAR keep their length
            if (pgType is "CHAR" or "VARCHAR")
                return $"{pgType}({length})";

            // TIMESTAMP with precision
            if (pgType.StartsWith("TIMESTAMP") || pgType == "TIME")
                return length <= 7 ? $"{pgType}({length})" : pgType;

            return pgType;
        }

        return pgType;
    }
}
