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

        // Post-conversion validation: ensure the output is structurally valid
        if (!PassesStructuralValidation(result))
        {
            return $"-- Manual conversion required: automated transform produced invalid SQL.\n" +
                   $"-- Original SQL Server pattern:\n" +
                   $"-- {sqlServerPattern.Replace("\n", "\n-- ")}\n" +
                   $"-- Detected features: {string.Join(", ", detectedFeatures)}\n" +
                   $"-- TODO: manually convert the above statement to valid PostgreSQL.";
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
    /// Handles nested function calls in the start/end arguments (e.g., GETDATE(), MAX()).
    /// </summary>
    internal static string ConvertDatediff(string sql)
    {
        // Use a manual approach to handle nested parentheses in DATEDIFF arguments
        var result = sql;
        var searchStart = 0;

        while (true)
        {
            var match = DatediffStartRegex().Match(result, searchStart);
            if (!match.Success)
                break;

            // Find the balanced parentheses content after DATEDIFF(
            var openParenIdx = match.Index + match.Length - 1; // index of the '(' in DATEDIFF(
            var (content, endIdx) = ExtractBalancedParenContent(result, openParenIdx);

            if (content is null)
            {
                searchStart = match.Index + match.Length;
                continue;
            }

            // Parse the three comma-separated arguments (respecting nested parens)
            var args = SplitDatediffArgs(content);
            if (args.Count != 3)
            {
                searchStart = match.Index + match.Length;
                continue;
            }

            var datepart = args[0].Trim().ToUpperInvariant();
            var startExpr = args[1].Trim();
            var endExpr = args[2].Trim();

            var replacement = datepart switch
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

            // Replace the entire DATEDIFF(...) with the conversion
            result = result[..match.Index] + replacement + result[(endIdx + 1)..];
            searchStart = match.Index + replacement.Length;
        }

        return result;
    }

    /// <summary>
    /// Extracts the content inside balanced parentheses starting at the given opening paren index.
    /// Returns the content (without outer parens) and the index of the closing paren.
    /// </summary>
    private static (string? Content, int EndIndex) ExtractBalancedParenContent(string sql, int openParenIdx)
    {
        if (openParenIdx >= sql.Length || sql[openParenIdx] != '(')
            return (null, -1);

        var depth = 0;
        for (int i = openParenIdx; i < sql.Length; i++)
        {
            if (sql[i] == '(') depth++;
            else if (sql[i] == ')') depth--;

            if (depth == 0)
            {
                var content = sql[(openParenIdx + 1)..i];
                return (content, i);
            }
        }
        return (null, -1);
    }

    /// <summary>
    /// Splits DATEDIFF arguments respecting nested parentheses.
    /// E.g., "DAY, MAX(o.OrderDate), GETDATE()" → ["DAY", "MAX(o.OrderDate)", "GETDATE()"]
    /// </summary>
    private static List<string> SplitDatediffArgs(string content)
    {
        var args = new List<string>();
        var depth = 0;
        var start = 0;

        for (int i = 0; i < content.Length; i++)
        {
            if (content[i] == '(') depth++;
            else if (content[i] == ')') depth--;
            else if (content[i] == ',' && depth == 0)
            {
                args.Add(content[start..i]);
                start = i + 1;
            }
        }
        args.Add(content[start..]);
        return args;
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
    /// Converts WITH (UPDLOCK) to SELECT ... FOR UPDATE for SELECT statements.
    /// For UPDATE/DELETE statements, UPDLOCK is simply removed since row-level locking
    /// is implicit under PostgreSQL's MVCC model.
    /// Risk 4: Requires review of locking strategy.
    /// </summary>
    internal static string ConvertUpdlock(string sql)
    {
        var result = UpdlockRegex().Replace(sql, "");
        if (result != sql)
        {
            // Determine if this is a SELECT statement or a DML (UPDATE/DELETE) statement
            var trimmedSql = result.TrimStart();
            var isSelect = trimmedSql.StartsWith("SELECT", StringComparison.OrdinalIgnoreCase);

            if (isSelect)
            {
                // FOR UPDATE is valid on SELECT statements in PostgreSQL
                result = result.TrimEnd().TrimEnd(';');
                result += "\nFOR UPDATE";
                result = "-- TODO: verify locking strategy; UPDLOCK replaced with SELECT ... FOR UPDATE\n" + result;
            }
            else
            {
                // FOR UPDATE is NOT valid on UPDATE/DELETE statements in PostgreSQL.
                // Row-level locking on DML is implicit under MVCC.
                // If pessimistic locking is required, it should be done via SELECT ... FOR UPDATE
                // in a preceding statement within the same transaction.
                result = "-- TODO: UPDLOCK removed. Row-level locking on UPDATE/DELETE is implicit in PostgreSQL's MVCC.\n" +
                         "-- If explicit pessimistic locking is required, use SELECT ... FOR UPDATE in a preceding\n" +
                         "-- statement within the same transaction, then perform the UPDATE/DELETE.\n" + result;
            }
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
    /// Extracts actual column assignments from WHEN MATCHED, uses consistent aliases,
    /// and explicitly flags any WHEN NOT MATCHED BY SOURCE THEN DELETE branch.
    /// </summary>
    internal static string ConvertMerge(string sql)
    {
        // Extract components from the MERGE statement
        var matchTarget = MergeTargetRegex().Match(sql);
        var targetTable = matchTarget.Success ? matchTarget.Groups["target"].Value : "target_table";

        var matchSource = MergeSourceRegex().Match(sql);
        var sourceTable = matchSource.Success ? matchSource.Groups["source"].Value : "source_table";

        var matchCondition = MergeOnRegex().Match(sql);
        var joinCondition = matchCondition.Success ? matchCondition.Groups["condition"].Value.Trim() : "t.id = s.id";

        // Extract key column from the ON condition for conflict target
        var keyColumn = ExtractKeyColumn(joinCondition);

        // Extract the source and target aliases used in the ON condition
        var (targetAlias, sourceAlias) = ExtractAliases(joinCondition, targetTable, sourceTable);

        // Extract UPDATE SET columns from WHEN MATCHED THEN UPDATE SET ...
        var updateColumns = ExtractUpdateSetColumns(sql, sourceAlias);

        // Extract INSERT columns from WHEN NOT MATCHED THEN INSERT (...) VALUES (...)
        var (insertColumns, insertValues) = ExtractInsertColumns(sql, sourceAlias);

        // Detect WHEN NOT MATCHED BY SOURCE THEN DELETE
        var hasDeleteBranch = MergeDeleteBranchRegex().IsMatch(sql);

        // Build the upsert statement
        var sb = new System.Text.StringBuilder();
        sb.AppendLine("-- TODO: verify conflict target column and update columns match your schema");
        sb.AppendLine("-- Original MERGE converted to INSERT ... ON CONFLICT (upsert pattern)");

        if (insertColumns.Length > 0 && insertValues.Length > 0)
        {
            sb.AppendLine($"INSERT INTO {targetTable} ({insertColumns})");
            sb.AppendLine($"SELECT {insertValues}");
            sb.AppendLine($"FROM {sourceTable} AS {sourceAlias}");
        }
        else
        {
            sb.AppendLine($"INSERT INTO {targetTable}");
            sb.AppendLine($"SELECT * FROM {sourceTable} AS {sourceAlias}");
        }

        sb.AppendLine($"ON CONFLICT ({keyColumn}) DO UPDATE SET");

        if (updateColumns.Length > 0)
        {
            sb.Append($"    {updateColumns}");
        }
        else
        {
            sb.Append("    -- TODO: list columns to update on conflict from the original WHEN MATCHED clause");
        }

        sb.AppendLine(";");

        // If a DELETE branch existed, emit it explicitly
        if (hasDeleteBranch)
        {
            sb.AppendLine();
            sb.AppendLine("-- WARNING: The original MERGE included a WHEN NOT MATCHED BY SOURCE THEN DELETE branch.");
            sb.AppendLine("-- This branch deletes rows in the target that have no corresponding source row.");
            sb.AppendLine("-- PostgreSQL INSERT ... ON CONFLICT cannot express this. A separate DELETE is required:");
            sb.AppendLine($"DELETE FROM {targetTable} AS {targetAlias}");
            sb.AppendLine($"WHERE NOT EXISTS (");
            sb.AppendLine($"    SELECT 1 FROM {sourceTable} AS {sourceAlias}");
            sb.AppendLine($"    WHERE {joinCondition}");
            sb.AppendLine($");");
            sb.AppendLine("-- TODO: verify this DELETE is correct for your business logic and add appropriate transaction handling.");
        }

        return sb.ToString().TrimEnd();
    }

    /// <summary>
    /// Extracts the target and source aliases from the MERGE statement's ON condition,
    /// or derives them from the MERGE ... AS alias syntax.
    /// </summary>
    private static (string TargetAlias, string SourceAlias) ExtractAliases(
        string joinCondition, string targetTable, string sourceTable)
    {
        // Try to detect aliases from the condition: "t.col = s.col" → (t, s)
        var aliasMatch = MergeAliasRegex().Match(joinCondition);
        if (aliasMatch.Success)
        {
            return (aliasMatch.Groups["left"].Value, aliasMatch.Groups["right"].Value);
        }

        // Default: use "t" and "s" as conventional short aliases
        return ("t", "s");
    }

    /// <summary>
    /// Extracts column assignments from the WHEN MATCHED THEN UPDATE SET clause.
    /// Returns them formatted for a DO UPDATE SET clause with the EXCLUDED pseudo-table.
    /// </summary>
    private static string ExtractUpdateSetColumns(string sql, string sourceAlias)
    {
        var match = MergeUpdateSetRegex().Match(sql);
        if (!match.Success)
            return "";

        var setClause = match.Groups["cols"].Value.Trim();

        // Convert source alias references (e.g., "s.Name") to EXCLUDED.Name
        // Pattern: replace sourceAlias.column with EXCLUDED.column
        var converted = Regex.Replace(
            setClause,
            $@"\b{Regex.Escape(sourceAlias)}\.(\w+)",
            "EXCLUDED.$1",
            RegexOptions.IgnoreCase);

        // Also strip target alias prefix (e.g., "t.Name = ..." → "Name = ...")
        converted = Regex.Replace(
            converted,
            @"\b\w+\.(\w+)\s*=",
            "$1 =",
            RegexOptions.IgnoreCase);

        return converted;
    }

    /// <summary>
    /// Extracts INSERT column list and VALUES from the WHEN NOT MATCHED THEN INSERT clause.
    /// </summary>
    private static (string Columns, string Values) ExtractInsertColumns(string sql, string sourceAlias)
    {
        var match = MergeInsertRegex().Match(sql);
        if (!match.Success)
            return ("", "");

        var columns = match.Groups["cols"].Value.Trim();
        var values = match.Groups["vals"].Value.Trim();

        // Replace source alias references in VALUES with plain column names for the SELECT
        var convertedValues = Regex.Replace(
            values,
            $@"\b{Regex.Escape(sourceAlias)}\.(\w+)",
            "$1",
            RegexOptions.IgnoreCase);

        return (columns, convertedValues);
    }

    /// <summary>
    /// Converts local temp tables (#name) to CREATE TEMPORARY TABLE with lifecycle notes.
    /// Risk 3: Session-scoping differences between SQL Server and PostgreSQL.
    /// </summary>
    internal static string ConvertTempTable(string sql)
    {
        var hasCreateTable = CreateTempTableRegex().IsMatch(sql);

        // Replace CREATE TABLE #name with CREATE TEMPORARY TABLE name
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

        // Add lifecycle TODO note if a CREATE TABLE was converted
        if (hasCreateTable)
        {
            result = "-- TODO: PostgreSQL temp tables are session-scoped (dropped at session end by default).\n" +
                     "-- Use ON COMMIT DROP for transaction-scoped behavior (similar to SQL Server batch-scoping).\n" +
                     "-- SQL Server drops local temp tables at the end of the batch/stored procedure; adjust lifecycle as needed.\n" +
                     result;
        }

        return result;
    }

    /// <summary>
    /// Converts global temp tables (##name) to unlogged permanent tables with TODO comments
    /// about the cross-session visibility gap and required lifecycle management.
    /// Risk 4: No direct equivalent; architectural decision required.
    /// </summary>
    internal static string ConvertGlobalTempTable(string sql)
    {
        var hasCreateTable = CreateGlobalTempTableRegex().IsMatch(sql);

        // Replace CREATE TABLE ##name with CREATE UNLOGGED TABLE name
        var result = CreateGlobalTempTableRegex().Replace(sql, match =>
        {
            var tableName = match.Groups["name"].Value;
            var rest = match.Groups["rest"].Value;
            return $"CREATE UNLOGGED TABLE {tableName}{rest}";
        });

        // Replace remaining ##references
        result = GlobalTempTableReferenceRegex().Replace(result, match =>
        {
            var tableName = match.Groups["name"].Value;
            return tableName;
        });

        // Add comprehensive TODO notes about the architectural gap
        if (hasCreateTable)
        {
            result = "-- TODO: PostgreSQL has NO equivalent of cross-session shared temp tables (##name).\n" +
                     "-- This uses an UNLOGGED TABLE (permanent, no WAL overhead) as a workaround.\n" +
                     "-- CRITICAL: Unlike SQL Server's automatic cleanup when the last session disconnects,\n" +
                     "-- this table persists until explicitly dropped. You MUST implement a cleanup strategy:\n" +
                     "-- Option A: Scheduled job (pg_cron) to TRUNCATE/DROP stale data\n" +
                     "-- Option B: Application-level cleanup with session tracking column\n" +
                     "-- Option C: Replace with application-level caching (Redis, etc.) if cross-session sharing is the goal\n" +
                     result;
        }
        else
        {
            // Just a reference to a global temp table (not the CREATE statement)
            result = "-- TODO: global temp table reference converted to regular table name.\n" +
                     "-- Ensure the table exists as a permanent/unlogged table with appropriate lifecycle management.\n" +
                     result;
        }

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

    /// <summary>
    /// Performs structural validation on the generated PostgreSQL SQL.
    /// Checks for common issues that would cause a parse failure:
    /// - Unbalanced parentheses
    /// - Unbalanced single quotes (outside comments)
    /// - FOR UPDATE appearing on non-SELECT statements
    /// - Empty output (all content stripped)
    /// </summary>
    internal static bool PassesStructuralValidation(string sql)
    {
        if (string.IsNullOrWhiteSpace(sql))
            return false;

        // Strip comment lines for validation purposes
        var sqlLines = sql.Split('\n')
            .Where(line => !line.TrimStart().StartsWith("--"))
            .ToList();
        var coreSQL = string.Join("\n", sqlLines).Trim();

        // If only comments remain, that's valid (it's a manual-conversion placeholder)
        if (string.IsNullOrWhiteSpace(coreSQL))
            return true;

        // Check balanced parentheses
        var parenDepth = 0;
        var inString = false;
        for (int i = 0; i < coreSQL.Length; i++)
        {
            var c = coreSQL[i];
            if (c == '\'' && !inString) { inString = true; continue; }
            if (c == '\'' && inString)
            {
                // Check for escaped quote ''
                if (i + 1 < coreSQL.Length && coreSQL[i + 1] == '\'') { i++; continue; }
                inString = false; continue;
            }
            if (inString) continue;

            if (c == '(') parenDepth++;
            else if (c == ')') parenDepth--;

            if (parenDepth < 0) return false; // More closing than opening
        }
        if (parenDepth != 0) return false;

        // Check that string quotes are balanced
        if (inString) return false;

        // Check FOR UPDATE is not on a non-SELECT statement
        if (coreSQL.Contains("FOR UPDATE", StringComparison.OrdinalIgnoreCase))
        {
            var trimmedCore = coreSQL.TrimStart();
            if (!trimmedCore.StartsWith("SELECT", StringComparison.OrdinalIgnoreCase))
            {
                // FOR UPDATE on a non-SELECT is invalid
                return false;
            }
        }

        return true;
    }

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

    [GeneratedRegex(@"\bDATEDIFF\s*\(", RegexOptions.IgnoreCase)]
    private static partial Regex DatediffStartRegex();

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

    [GeneratedRegex(@"(?<left>\w+)\.(\w+)\s*=\s*(?<right>\w+)\.\1")]
    private static partial Regex MergeAliasRegex();

    [GeneratedRegex(@"WHEN\s+MATCHED\s+THEN\s+UPDATE\s+SET\s+(?<cols>.+?)(?=\s+WHEN\b|\s*;|\s*$)", RegexOptions.IgnoreCase | RegexOptions.Singleline)]
    private static partial Regex MergeUpdateSetRegex();

    [GeneratedRegex(@"WHEN\s+NOT\s+MATCHED\s+(?:BY\s+TARGET\s+)?THEN\s+INSERT\s*\((?<cols>[^)]+)\)\s*VALUES\s*\((?<vals>[^)]+)\)", RegexOptions.IgnoreCase | RegexOptions.Singleline)]
    private static partial Regex MergeInsertRegex();

    [GeneratedRegex(@"WHEN\s+NOT\s+MATCHED\s+BY\s+SOURCE\s+THEN\s+DELETE", RegexOptions.IgnoreCase)]
    private static partial Regex MergeDeleteBranchRegex();

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
