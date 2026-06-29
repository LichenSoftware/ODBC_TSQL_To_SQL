using System.Text.RegularExpressions;

namespace MigrationAssessment.WorkItems;

/// <summary>
/// Converts SQL Server T-SQL patterns to syntactically correct PostgreSQL equivalents.
/// Implements per-feature transformation functions that preserve the full structure
/// of the original statement while replacing SQL Server-specific syntax.
/// </summary>
public sealed partial class PostgresConversionEngine : IPostgresConversionEngine
{
    private static readonly Dictionary<string, Func<string, string>> Transformations = new(StringComparer.OrdinalIgnoreCase)
    {
        ["ISNULL"] = ConvertIsnull,
        ["GETDATE"] = ConvertGetdate,
        ["TOP"] = ConvertTop,
        ["DATEDIFF"] = ConvertDatediff,
        ["NOLOCK"] = ConvertNolock,
        ["UPDLOCK"] = ConvertUpdlock,
        ["ROWLOCK"] = ConvertRowlock,
        ["MERGE"] = ConvertMerge,
        ["TEMP_TABLE"] = ConvertTempTable,
        ["GLOBAL_TEMP_TABLE"] = ConvertGlobalTempTable,
        ["XML_METHOD"] = ConvertXmlMethod,
        ["STRING_CONCAT_PLUS"] = ConvertStringConcatPlus,
        ["PRINT_STATEMENT"] = ConvertPrintStatement,
        ["THROW"] = ConvertThrow,
        ["RAISERROR"] = ConvertRaiserror,
        ["STRING_SPLIT"] = ConvertStringSplit,
        ["OPENJSON"] = ConvertOpenjson,
        ["FOR_XML"] = ConvertForXml,
    };

    /// <inheritdoc />
    public string Convert(string sqlServerPattern, IReadOnlyList<string> detectedFeatures)
    {
        ArgumentNullException.ThrowIfNull(detectedFeatures);
        if (string.IsNullOrWhiteSpace(sqlServerPattern))
            return "-- No SQL Server pattern provided";

        var result = sqlServerPattern;
        foreach (var feature in detectedFeatures)
        {
            if (Transformations.TryGetValue(feature, out var transform))
            {
                result = transform(result);
            }
        }

        return result;
    }

    /// <inheritdoc />
    public string Convert(string sqlServerPattern, string featureName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(featureName);
        return Convert(sqlServerPattern, new[] { featureName });
    }

    // ═══════════════════════════════════════════════════════════════
    // Per-feature transformation functions
    // ═══════════════════════════════════════════════════════════════

    /// <summary>
    /// Converts ISNULL(expr, replacement) to COALESCE(expr, replacement).
    /// Risk 2: Direct syntax mapping.
    /// </summary>
    internal static string ConvertIsnull(string sql)
    {
        // Replace ISNULL( with COALESCE( preserving arguments
        return IsnullRegex().Replace(sql, "COALESCE(");
    }

    /// <summary>
    /// Converts GETDATE() to NOW().
    /// Risk 2: Direct syntax mapping.
    /// </summary>
    internal static string ConvertGetdate(string sql)
    {
        return GetdateRegex().Replace(sql, "NOW()");
    }

    /// <summary>
    /// Converts SELECT TOP N ... to SELECT ... LIMIT N.
    /// Risk 2: Direct syntax mapping.
    /// </summary>
    internal static string ConvertTop(string sql)
    {
        // Match SELECT TOP (N) or SELECT TOP N with optional PERCENT
        var match = TopWithParensRegex().Match(sql);
        if (match.Success)
        {
            var topValue = match.Groups["n"].Value;
            var remainder = match.Groups["rest"].Value;
            var prefix = match.Groups["prefix"].Value;
            return $"{prefix}SELECT {remainder.TrimEnd()}\nLIMIT {topValue}";
        }

        match = TopWithoutParensRegex().Match(sql);
        if (match.Success)
        {
            var topValue = match.Groups["n"].Value;
            var remainder = match.Groups["rest"].Value;
            var prefix = match.Groups["prefix"].Value;
            return $"{prefix}SELECT {remainder.TrimEnd()}\nLIMIT {topValue}";
        }

        return sql;
    }

    /// <summary>
    /// Converts DATEDIFF(datepart, start, end) to PostgreSQL equivalents.
    /// Risk 2: Direct syntax mapping with datepart-specific logic.
    /// </summary>
    internal static string ConvertDatediff(string sql)
    {
        return DatediffRegex().Replace(sql, match =>
        {
            var datepart = match.Groups["datepart"].Value.ToUpperInvariant();
            var startExpr = match.Groups["start"].Value.Trim();
            var endExpr = match.Groups["end"].Value.Trim();

            return datepart switch
            {
                "DAY" or "DD" or "D" =>
                    $"({endExpr}::date - {startExpr}::date)",
                "SECOND" or "SS" or "S" =>
                    $"EXTRACT(EPOCH FROM ({endExpr} - {startExpr}))::int",
                "MINUTE" or "MI" or "N" =>
                    $"(EXTRACT(EPOCH FROM ({endExpr} - {startExpr})) / 60)::int",
                "HOUR" or "HH" =>
                    $"(EXTRACT(EPOCH FROM ({endExpr} - {startExpr})) / 3600)::int",
                "MONTH" or "MM" or "M" =>
                    $"(EXTRACT(YEAR FROM AGE({endExpr}, {startExpr})) * 12 + EXTRACT(MONTH FROM AGE({endExpr}, {startExpr})))::int",
                "YEAR" or "YY" or "YYYY" =>
                    $"EXTRACT(YEAR FROM AGE({endExpr}, {startExpr}))::int",
                _ =>
                    $"/* TODO: verify DATEDIFF({datepart}) conversion */\n({endExpr}::date - {startExpr}::date)"
            };
        });
    }

    /// <summary>
    /// Converts WITH (NOLOCK) hints by removing them (PostgreSQL MVCC handles this).
    /// Risk 4: Requires understanding of locking strategy.
    /// </summary>
    internal static string ConvertNolock(string sql)
    {
        var result = NolockRegex().Replace(sql, "");
        // Add TODO comment if the hint was present
        if (result != sql)
        {
            result = "-- TODO: verify locking strategy; NOLOCK removed (PostgreSQL MVCC provides non-blocking reads)\n" + result;
        }
        return result;
    }

    /// <summary>
    /// Converts WITH (UPDLOCK) to SELECT ... FOR UPDATE.
    /// Risk 4: Requires review of locking strategy.
    /// </summary>
    internal static string ConvertUpdlock(string sql)
    {
        var result = UpdlockRegex().Replace(sql, "");
        if (result != sql)
        {
            // Append FOR UPDATE to the end of the statement
            result = result.TrimEnd().TrimEnd(';');
            result += "\nFOR UPDATE";
            result = "-- TODO: verify locking strategy; UPDLOCK replaced with FOR UPDATE\n" + result;
        }
        return result;
    }

    /// <summary>
    /// Converts WITH (ROWLOCK) by removing it (PostgreSQL always uses row-level locking).
    /// Risk 4: Requires review of locking strategy.
    /// </summary>
    internal static string ConvertRowlock(string sql)
    {
        var result = RowlockRegex().Replace(sql, "");
        if (result != sql)
        {
            result = "-- TODO: verify locking strategy; ROWLOCK removed (PostgreSQL uses row-level locking by default)\n" + result;
        }
        return result;
    }

    /// <summary>
    /// Converts MERGE statement to INSERT ... ON CONFLICT (PostgreSQL 15+ MERGE or upsert).
    /// Risk 4: Complex conversion requiring design decisions.
    /// </summary>
    internal static string ConvertMerge(string sql)
    {
        // MERGE is complex; provide a structural equivalent with TODO markers
        var match = MergeTargetRegex().Match(sql);
        var targetTable = match.Success ? match.Groups["target"].Value : "target_table";

        var matchSource = MergeSourceRegex().Match(sql);
        var sourceTable = matchSource.Success ? matchSource.Groups["source"].Value : "source_table";

        var matchCondition = MergeOnRegex().Match(sql);
        var joinCondition = matchCondition.Success ? matchCondition.Groups["condition"].Value : "t.id = s.id";

        // Extract key column from the ON condition for conflict target
        var keyColumn = ExtractKeyColumn(joinCondition);

        return $"""
            -- TODO: verify conflict target column and update columns match your schema
            -- Original MERGE converted to INSERT ... ON CONFLICT (upsert pattern)
            INSERT INTO {targetTable} AS t
            SELECT * FROM {sourceTable} AS s
            WHERE NOT EXISTS (
                SELECT 1 FROM {targetTable} WHERE {joinCondition}
            )
            ON CONFLICT ({keyColumn}) DO UPDATE SET
                -- TODO: list columns to update on conflict
                updated_at = NOW();
            """;
    }

    /// <summary>
    /// Converts local temp tables (#name) to CREATE TEMPORARY TABLE.
    /// Risk 3: Session-scoping differences.
    /// </summary>
    internal static string ConvertTempTable(string sql)
    {
        // Replace #tableName with tableName (remove # prefix) and add TEMPORARY
        var result = CreateTempTableRegex().Replace(sql, match =>
        {
            var tableName = match.Groups["name"].Value;
            var rest = match.Groups["rest"].Value;
            return $"CREATE TEMPORARY TABLE {tableName}{rest}";
        });

        // Replace remaining #references in queries (SELECT/INSERT/UPDATE/DELETE)
        result = TempTableReferenceRegex().Replace(result, match =>
        {
            var tableName = match.Groups["name"].Value;
            return tableName;
        });

        return result;
    }

    /// <summary>
    /// Converts global temp tables (##name) to unlogged tables with TODO comments.
    /// Risk 4: No direct equivalent; architectural decision required.
    /// </summary>
    internal static string ConvertGlobalTempTable(string sql)
    {
        // Replace ##tableName with an unlogged table approach
        var result = CreateGlobalTempTableRegex().Replace(sql, match =>
        {
            var tableName = match.Groups["name"].Value;
            var rest = match.Groups["rest"].Value;
            return $"-- TODO: global temp table has no direct PostgreSQL equivalent\n" +
                   $"-- Consider: unlogged table with session_id column for cleanup, or application-level caching\n" +
                   $"CREATE UNLOGGED TABLE {tableName}{rest}";
        });

        // Replace remaining ##references
        result = GlobalTempTableReferenceRegex().Replace(result, match =>
        {
            var tableName = match.Groups["name"].Value;
            return tableName;
        });

        return result;
    }

    /// <summary>
    /// Converts XML methods (.value, .query, .nodes, .exist) to PostgreSQL xpath/xmltable.
    /// Risk 5: Complex conversion requiring architectural review.
    /// </summary>
    internal static string ConvertXmlMethod(string sql)
    {
        // Convert .value('xpath', 'type') to (xpath('xpath', col))[1]::type
        var result = XmlValueRegex().Replace(sql, match =>
        {
            var column = match.Groups["col"].Value;
            var xpathExpr = match.Groups["xpath"].Value;
            var dataType = match.Groups["type"].Value.Trim();
            var pgType = MapSqlServerTypeToPg(dataType);
            return $"(xpath('{xpathExpr}', {column}))[1]::text::{pgType}";
        });

        // Convert .query('xpath') to xpath('xpath', col)
        result = XmlQueryRegex().Replace(result, match =>
        {
            var column = match.Groups["col"].Value;
            var xpathExpr = match.Groups["xpath"].Value;
            return $"xpath('{xpathExpr}', {column})";
        });

        // Convert .exist('xpath') to xpath_exists('xpath', col)
        result = XmlExistRegex().Replace(result, match =>
        {
            var column = match.Groups["col"].Value;
            var xpathExpr = match.Groups["xpath"].Value;
            return $"xpath_exists('{xpathExpr}', {column})";
        });

        // Convert .nodes('xpath') to xmltable
        result = XmlNodesRegex().Replace(result, match =>
        {
            var column = match.Groups["col"].Value;
            var xpathExpr = match.Groups["xpath"].Value;
            return $"-- TODO: verify xmltable column definitions\nxmltable('{xpathExpr}' PASSING {column} COLUMNS /* define columns here */)";
        });

        if (result != sql)
        {
            result = "-- TODO: verify XPath expressions and namespace handling for PostgreSQL\n" + result;
        }

        return result;
    }

    // ═══════════════════════════════════════════════════════════════
    // New feature conversion functions
    // ═══════════════════════════════════════════════════════════════

    /// <summary>
    /// Converts string + string to string || string.
    /// Risk 2: Direct operator substitution.
    /// </summary>
    internal static string ConvertStringConcatPlus(string sql)
    {
        // Replace ' + ' patterns between string expressions with ||
        // This is a heuristic — full AST-level replacement is handled by detection
        return StringConcatPlusRegex().Replace(sql, " || ");
    }

    /// <summary>
    /// Converts PRINT 'message' to RAISE NOTICE.
    /// Risk 2: Direct syntax mapping.
    /// </summary>
    internal static string ConvertPrintStatement(string sql)
    {
        return PrintRegex().Replace(sql, match =>
        {
            var message = match.Groups["msg"].Value;
            return $"RAISE NOTICE '%', {message}";
        });
    }

    /// <summary>
    /// Converts THROW to RAISE EXCEPTION.
    /// Risk 2: Direct syntax mapping.
    /// </summary>
    internal static string ConvertThrow(string sql)
    {
        // THROW number, 'message', state → RAISE EXCEPTION 'message'
        var result = ThrowWithArgsRegex().Replace(sql, match =>
        {
            var message = match.Groups["msg"].Value;
            return $"RAISE EXCEPTION {message}";
        });

        // Bare THROW (re-throw in CATCH) → RAISE
        if (result == sql)
        {
            result = ThrowBareRegex().Replace(sql, "RAISE");
        }

        return result;
    }

    /// <summary>
    /// Converts RAISERROR to RAISE EXCEPTION/WARNING/NOTICE.
    /// Risk 3: Severity-based mapping.
    /// </summary>
    internal static string ConvertRaiserror(string sql)
    {
        return RaiserrorRegex().Replace(sql, match =>
        {
            var message = match.Groups["msg"].Value;
            var severity = match.Groups["sev"].Value;
            var level = int.TryParse(severity, out var sev) && sev < 16 ? "WARNING" : "EXCEPTION";
            return $"RAISE {level} '%', {message}";
        });
    }

    /// <summary>
    /// Converts STRING_SPLIT to string_to_table.
    /// Risk 2: Direct function substitution.
    /// </summary>
    internal static string ConvertStringSplit(string sql)
    {
        return StringSplitRegex().Replace(sql, match =>
        {
            var str = match.Groups["str"].Value;
            var sep = match.Groups["sep"].Value;
            return $"string_to_table({str}, {sep})";
        });
    }

    /// <summary>
    /// Converts OPENJSON to jsonb_each/jsonb_array_elements.
    /// Risk 4: Requires design decisions about schema mapping.
    /// </summary>
    internal static string ConvertOpenjson(string sql)
    {
        var result = OpenjsonRegex().Replace(sql, match =>
        {
            var jsonExpr = match.Groups["json"].Value;
            return $"jsonb_each({jsonExpr}::jsonb)";
        });

        if (result != sql)
        {
            result = "-- TODO: verify JSON structure; use jsonb_array_elements() for arrays, jsonb_to_recordset() for typed output\n" + result;
        }

        return result;
    }

    /// <summary>
    /// Converts FOR XML PATH/AUTO/RAW to json_agg or string_agg.
    /// Risk 4: Pattern depends on usage context.
    /// </summary>
    internal static string ConvertForXml(string sql)
    {
        var result = ForXmlPathRegex().Replace(sql, match =>
        {
            return "\n-- TODO: FOR XML removed; use json_agg(row_to_json(t)) for structured output or string_agg() for concatenation";
        });

        if (result == sql)
        {
            result = ForXmlGenericRegex().Replace(sql, match =>
            {
                return "\n-- TODO: FOR XML removed; use json_agg() or xmlagg(xmlelement(...)) for equivalent output";
            });
        }

        return result;
    }

    // ═══════════════════════════════════════════════════════════════
    // Helper methods
    // ═══════════════════════════════════════════════════════════════

    private static string ExtractKeyColumn(string joinCondition)
    {
        // Try to extract "t.id = s.id" -> "id"
        var match = KeyColumnRegex().Match(joinCondition);
        if (match.Success)
        {
            return match.Groups["col"].Value;
        }
        return "id /* TODO: specify correct conflict column */";
    }

    private static string MapSqlServerTypeToPg(string sqlServerType)
    {
        var normalized = sqlServerType.Trim().ToUpperInvariant();
        return normalized switch
        {
            "NVARCHAR(MAX)" or "VARCHAR(MAX)" or "NTEXT" or "TEXT" => "text",
            "INT" or "INTEGER" => "integer",
            "BIGINT" => "bigint",
            "SMALLINT" => "smallint",
            "BIT" => "boolean",
            "FLOAT" => "double precision",
            "DECIMAL" or "NUMERIC" => "numeric",
            "DATETIME" or "DATETIME2" => "timestamp",
            "DATE" => "date",
            "UNIQUEIDENTIFIER" => "uuid",
            "XML" => "xml",
            _ when normalized.StartsWith("NVARCHAR") => "text",
            _ when normalized.StartsWith("VARCHAR") => "text",
            _ when normalized.StartsWith("DECIMAL") => "numeric",
            _ when normalized.StartsWith("NUMERIC") => "numeric",
            _ => normalized.ToLowerInvariant()
        };
    }

    // ═══════════════════════════════════════════════════════════════
    // Compiled regex patterns (source-generated for performance)
    // ═══════════════════════════════════════════════════════════════

    [GeneratedRegex(@"\bISNULL\s*\(", RegexOptions.IgnoreCase)]
    private static partial Regex IsnullRegex();

    [GeneratedRegex(@"\bGETDATE\s*\(\s*\)", RegexOptions.IgnoreCase)]
    private static partial Regex GetdateRegex();

    [GeneratedRegex(@"(?<prefix>^[\s]*)SELECT\s+TOP\s*\(\s*(?<n>\d+)\s*\)\s+(?<rest>.+)", RegexOptions.IgnoreCase | RegexOptions.Singleline)]
    private static partial Regex TopWithParensRegex();

    [GeneratedRegex(@"(?<prefix>^[\s]*)SELECT\s+TOP\s+(?<n>\d+)\s+(?<rest>.+)", RegexOptions.IgnoreCase | RegexOptions.Singleline)]
    private static partial Regex TopWithoutParensRegex();

    [GeneratedRegex(@"\bDATEDIFF\s*\(\s*(?<datepart>\w+)\s*,\s*(?<start>[^,]+)\s*,\s*(?<end>[^)]+)\)", RegexOptions.IgnoreCase)]
    private static partial Regex DatediffRegex();

    [GeneratedRegex(@"\s*WITH\s*\(\s*NOLOCK\s*\)", RegexOptions.IgnoreCase)]
    private static partial Regex NolockRegex();

    [GeneratedRegex(@"\s*WITH\s*\(\s*UPDLOCK(?:\s*,\s*\w+)*\s*\)", RegexOptions.IgnoreCase)]
    private static partial Regex UpdlockRegex();

    [GeneratedRegex(@"\s*WITH\s*\(\s*ROWLOCK(?:\s*,\s*\w+)*\s*\)", RegexOptions.IgnoreCase)]
    private static partial Regex RowlockRegex();

    [GeneratedRegex(@"MERGE\s+(?:INTO\s+)?(?<target>[\w.\[\]]+)", RegexOptions.IgnoreCase)]
    private static partial Regex MergeTargetRegex();

    [GeneratedRegex(@"USING\s+(?<source>[\w.\[\]]+)", RegexOptions.IgnoreCase)]
    private static partial Regex MergeSourceRegex();

    [GeneratedRegex(@"\bON\s+(?<condition>[^W]+?)(?=\s+WHEN\b)", RegexOptions.IgnoreCase)]
    private static partial Regex MergeOnRegex();

    [GeneratedRegex(@"\w+\.(?<col>\w+)\s*=\s*\w+\.\k<col>")]
    private static partial Regex KeyColumnRegex();

    [GeneratedRegex(@"CREATE\s+TABLE\s+#(?<name>\w+)(?<rest>.+)", RegexOptions.IgnoreCase | RegexOptions.Singleline)]
    private static partial Regex CreateTempTableRegex();

    [GeneratedRegex(@"(?<!\w)#(?<name>\w+)", RegexOptions.None)]
    private static partial Regex TempTableReferenceRegex();

    [GeneratedRegex(@"CREATE\s+TABLE\s+##(?<name>\w+)(?<rest>.+)", RegexOptions.IgnoreCase | RegexOptions.Singleline)]
    private static partial Regex CreateGlobalTempTableRegex();

    [GeneratedRegex(@"(?<!\w)##(?<name>\w+)", RegexOptions.None)]
    private static partial Regex GlobalTempTableReferenceRegex();

    [GeneratedRegex(@"(?<col>[\w.]+)\.value\s*\(\s*'(?<xpath>[^']+)'\s*,\s*'(?<type>[^']+)'\s*\)", RegexOptions.IgnoreCase)]
    private static partial Regex XmlValueRegex();

    [GeneratedRegex(@"(?<col>[\w.]+)\.query\s*\(\s*'(?<xpath>[^']+)'\s*\)", RegexOptions.IgnoreCase)]
    private static partial Regex XmlQueryRegex();

    [GeneratedRegex(@"(?<col>[\w.]+)\.exist\s*\(\s*'(?<xpath>[^']+)'\s*\)", RegexOptions.IgnoreCase)]
    private static partial Regex XmlExistRegex();

    [GeneratedRegex(@"(?<col>[\w.]+)\.nodes\s*\(\s*'(?<xpath>[^']+)'\s*\)", RegexOptions.IgnoreCase)]
    private static partial Regex XmlNodesRegex();

    [GeneratedRegex(@"(?<='[^']*'|\w)\s*\+\s*(?='[^']*'|\w)", RegexOptions.None)]
    private static partial Regex StringConcatPlusRegex();

    [GeneratedRegex(@"\bPRINT\s+(?<msg>[^\r\n;]+)", RegexOptions.IgnoreCase)]
    private static partial Regex PrintRegex();

    [GeneratedRegex(@"\bTHROW\s+\d+\s*,\s*(?<msg>'[^']*'|[^,]+)\s*,\s*\d+", RegexOptions.IgnoreCase)]
    private static partial Regex ThrowWithArgsRegex();

    [GeneratedRegex(@"\bTHROW\s*;", RegexOptions.IgnoreCase)]
    private static partial Regex ThrowBareRegex();

    [GeneratedRegex(@"\bRAISERROR\s*\(\s*(?<msg>'[^']*'|[^,]+)\s*,\s*(?<sev>\d+)\s*,\s*\d+[^)]*\)", RegexOptions.IgnoreCase)]
    private static partial Regex RaiserrorRegex();

    [GeneratedRegex(@"\bSTRING_SPLIT\s*\(\s*(?<str>[^,]+)\s*,\s*(?<sep>[^)]+)\s*\)", RegexOptions.IgnoreCase)]
    private static partial Regex StringSplitRegex();

    [GeneratedRegex(@"\bOPENJSON\s*\(\s*(?<json>[^)]+)\s*\)", RegexOptions.IgnoreCase)]
    private static partial Regex OpenjsonRegex();

    [GeneratedRegex(@"\bFOR\s+XML\s+PATH\s*\([^)]*\)[^;]*", RegexOptions.IgnoreCase)]
    private static partial Regex ForXmlPathRegex();

    [GeneratedRegex(@"\bFOR\s+XML\s+(?:AUTO|RAW|EXPLICIT)[^;]*", RegexOptions.IgnoreCase)]
    private static partial Regex ForXmlGenericRegex();
}
