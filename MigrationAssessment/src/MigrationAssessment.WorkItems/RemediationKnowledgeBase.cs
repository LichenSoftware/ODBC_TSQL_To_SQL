using MigrationAssessment.WorkItems.Models;

namespace MigrationAssessment.WorkItems;

/// <summary>
/// Static knowledge base providing remediation guidance for known SQL Server features.
/// Entries are keyed by feature name (case-insensitive) and organized by risk level:
/// Risk 2: Direct syntax mappings, Risk 3: Step-by-step conversions,
/// Risk 4: Design pattern recommendations, Risk 5: Migration strategies.
/// Returns null for unknown features (triggers "requires-research" flag).
/// </summary>
public sealed class RemediationKnowledgeBase : IRemediationKnowledgeBase
{
    private static readonly Dictionary<string, RemediationEntry> Entries = new(StringComparer.OrdinalIgnoreCase)
    {
        // ═══════════════════════════════════════════════════════════════
        // Risk 2: Direct syntax mappings
        // ═══════════════════════════════════════════════════════════════

        ["TOP"] = new RemediationEntry
        {
            PostgresEquivalent = "LIMIT / OFFSET",
            RemediationSteps = "Replace SELECT TOP N with SELECT ... LIMIT N. For TOP N with ties, use FETCH FIRST N ROWS WITH TIES. For TOP with PERCENT, calculate the row count or use a window function.",
            IncompatibilityExplanation = "PostgreSQL does not support the TOP keyword. The equivalent functionality is provided by the LIMIT and OFFSET clauses placed at the end of the query.",
            RiskLevel = 2,
            RequiresArchitecturalReview = false,
            PostgresDocReference = "https://www.postgresql.org/docs/current/queries-limit.html"
        },

        ["ISNULL"] = new RemediationEntry
        {
            PostgresEquivalent = "COALESCE",
            RemediationSteps = "Replace ISNULL(expr, replacement) with COALESCE(expr, replacement). COALESCE is ANSI SQL standard and supports multiple fallback arguments.",
            IncompatibilityExplanation = "ISNULL is a SQL Server-specific function. PostgreSQL uses the ANSI-standard COALESCE function which accepts two or more arguments and returns the first non-null value.",
            RiskLevel = 2,
            RequiresArchitecturalReview = false,
            PostgresDocReference = "https://www.postgresql.org/docs/current/functions-conditional.html#FUNCTIONS-COALESCE-NVL-IFNULL"
        },

        ["GETDATE"] = new RemediationEntry
        {
            PostgresEquivalent = "NOW() / CURRENT_TIMESTAMP",
            RemediationSteps = "Replace GETDATE() with NOW() or CURRENT_TIMESTAMP. Use CLOCK_TIMESTAMP() if you need the actual current time within a transaction (NOW() returns transaction start time).",
            IncompatibilityExplanation = "GETDATE() is SQL Server-specific. PostgreSQL provides NOW() and CURRENT_TIMESTAMP which return the current date and time. Note that NOW() in PostgreSQL returns the transaction start time, not wall-clock time.",
            RiskLevel = 2,
            RequiresArchitecturalReview = false,
            PostgresDocReference = "https://www.postgresql.org/docs/current/functions-datetime.html#FUNCTIONS-DATETIME-CURRENT"
        },

        ["LEN"] = new RemediationEntry
        {
            PostgresEquivalent = "LENGTH",
            RemediationSteps = "Replace LEN(string) with LENGTH(string). Note that LENGTH in PostgreSQL does not trim trailing spaces, whereas LEN in SQL Server does. Use LENGTH(RTRIM(string)) for identical behavior.",
            IncompatibilityExplanation = "LEN is SQL Server-specific. PostgreSQL uses LENGTH for character length. A behavioral difference exists: SQL Server's LEN trims trailing spaces before counting, while PostgreSQL's LENGTH does not.",
            RiskLevel = 2,
            RequiresArchitecturalReview = false,
            PostgresDocReference = "https://www.postgresql.org/docs/current/functions-string.html"
        },

        ["CHARINDEX"] = new RemediationEntry
        {
            PostgresEquivalent = "POSITION / STRPOS",
            RemediationSteps = "Replace CHARINDEX(substring, string) with POSITION(substring IN string) or STRPOS(string, substring). For the three-argument form CHARINDEX(sub, str, start), use STRPOS(SUBSTRING(str FROM start), sub) + start - 1.",
            IncompatibilityExplanation = "CHARINDEX is SQL Server-specific. PostgreSQL provides POSITION (ANSI SQL) and STRPOS for locating substrings. The argument order differs between CHARINDEX and STRPOS.",
            RiskLevel = 2,
            RequiresArchitecturalReview = false,
            PostgresDocReference = "https://www.postgresql.org/docs/current/functions-string.html"
        },

        ["PATINDEX"] = new RemediationEntry
        {
            PostgresEquivalent = "Regex with ~ operator or SUBSTRING with SIMILAR TO",
            RemediationSteps = "Replace PATINDEX('%pattern%', string) with a regex-based approach. Use (CASE WHEN string ~ 'pattern' THEN ...) or write a custom function using regexp_instr (PostgreSQL 15+). For simple patterns, POSITION may suffice.",
            IncompatibilityExplanation = "PATINDEX uses SQL Server's wildcard pattern matching (%, _, []) to find positions. PostgreSQL uses POSIX regular expressions instead of SQL Server wildcards. Pattern syntax must be converted.",
            RiskLevel = 2,
            RequiresArchitecturalReview = false,
            PostgresDocReference = "https://www.postgresql.org/docs/current/functions-matching.html"
        },

        ["STUFF"] = new RemediationEntry
        {
            PostgresEquivalent = "OVERLAY",
            RemediationSteps = "Replace STUFF(string, start, length, replacement) with OVERLAY(string PLACING replacement FROM start FOR length). The semantics are equivalent.",
            IncompatibilityExplanation = "STUFF is SQL Server-specific for inserting a string into another string at a specified position after deleting a specified length. PostgreSQL uses the ANSI-standard OVERLAY function with identical semantics.",
            RiskLevel = 2,
            RequiresArchitecturalReview = false,
            PostgresDocReference = "https://www.postgresql.org/docs/current/functions-string.html"
        },

        ["DATEADD"] = new RemediationEntry
        {
            PostgresEquivalent = "Interval arithmetic (e.g., timestamp + INTERVAL '1 day')",
            RemediationSteps = "Replace DATEADD(datepart, number, date) with date + INTERVAL 'N units'. For example: DATEADD(day, 7, @date) becomes @date + INTERVAL '7 days'. Map dateparts: year→years, month→months, day→days, hour→hours, minute→minutes, second→seconds.",
            IncompatibilityExplanation = "SQL Server uses the DATEADD function with datepart keywords. PostgreSQL uses interval arithmetic with the + or - operators and INTERVAL literals, which is more flexible and ANSI-compliant.",
            RiskLevel = 2,
            RequiresArchitecturalReview = false,
            PostgresDocReference = "https://www.postgresql.org/docs/current/functions-datetime.html"
        },

        ["DATEDIFF"] = new RemediationEntry
        {
            PostgresEquivalent = "EXTRACT(EPOCH FROM ...) / AGE / date subtraction",
            RemediationSteps = "Replace DATEDIFF(datepart, start, end) based on the datepart: For days: (end::date - start::date). For seconds: EXTRACT(EPOCH FROM (end - start)). For months/years: use AGE(end, start) and extract components. Create a helper function for complex datepart calculations.",
            IncompatibilityExplanation = "SQL Server's DATEDIFF returns the count of datepart boundaries crossed between two dates. PostgreSQL has no direct equivalent; the approach varies by datepart and whether boundary-crossing or elapsed-time semantics are needed.",
            RiskLevel = 2,
            RequiresArchitecturalReview = false,
            PostgresDocReference = "https://www.postgresql.org/docs/current/functions-datetime.html"
        },

        ["DATEPART"] = new RemediationEntry
        {
            PostgresEquivalent = "EXTRACT(field FROM timestamp)",
            RemediationSteps = "Replace DATEPART(datepart, date) with EXTRACT(field FROM date). Map dateparts: year→YEAR, month→MONTH, day→DAY, hour→HOUR, minute→MINUTE, second→SECOND, weekday→DOW (note: DOW is 0=Sunday in PostgreSQL vs 1=Sunday in SQL Server).",
            IncompatibilityExplanation = "DATEPART is SQL Server-specific. PostgreSQL uses the ANSI-standard EXTRACT function. Weekday numbering differs: SQL Server uses 1-7 (Sunday=1) by default, PostgreSQL DOW uses 0-6 (Sunday=0).",
            RiskLevel = 2,
            RequiresArchitecturalReview = false,
            PostgresDocReference = "https://www.postgresql.org/docs/current/functions-datetime.html#FUNCTIONS-DATETIME-EXTRACT"
        },

        ["STRING_CONCAT_PLUS"] = new RemediationEntry
        {
            PostgresEquivalent = "|| (concatenation operator)",
            RemediationSteps = "Replace string + string with string || string. Note: PostgreSQL's || operator returns NULL if any operand is NULL (unlike SQL Server's + which may concatenate with NULL depending on settings). Use CONCAT() for NULL-safe concatenation.",
            IncompatibilityExplanation = "SQL Server uses + for both addition and string concatenation depending on operand types. PostgreSQL uses || exclusively for string concatenation. NULL handling differs: SET CONCAT_NULL_YIELDS_NULL behavior has no PostgreSQL equivalent.",
            RiskLevel = 2,
            RequiresArchitecturalReview = false,
            PostgresDocReference = "https://www.postgresql.org/docs/current/functions-string.html"
        },

        ["TOP_WITHOUT_ORDER"] = new RemediationEntry
        {
            PostgresEquivalent = "LIMIT N (with explicit ORDER BY for deterministic results)",
            RemediationSteps = "Replace SELECT TOP N with SELECT ... ORDER BY column LIMIT N. Without ORDER BY, the result set is non-deterministic in both SQL Server and PostgreSQL. Add an explicit ORDER BY clause to ensure consistent results across executions.",
            IncompatibilityExplanation = "TOP without ORDER BY returns an arbitrary subset of rows. While this works identically in PostgreSQL (LIMIT without ORDER BY), it indicates a potential bug. Add ORDER BY for deterministic pagination.",
            RiskLevel = 2,
            RequiresArchitecturalReview = false,
            PostgresDocReference = "https://www.postgresql.org/docs/current/queries-limit.html"
        },

        ["PRINT_STATEMENT"] = new RemediationEntry
        {
            PostgresEquivalent = "RAISE NOTICE 'message'",
            RemediationSteps = "Replace PRINT 'message' with RAISE NOTICE '%', 'message' in PL/pgSQL. RAISE NOTICE sends output to the client's notice channel. For formatted output, use RAISE NOTICE 'format %s', variable. Note: RAISE NOTICE is only available within PL/pgSQL functions or DO blocks.",
            IncompatibilityExplanation = "SQL Server's PRINT statement outputs messages to the client message stream. PostgreSQL uses RAISE NOTICE within PL/pgSQL for equivalent functionality. Plain SQL has no PRINT equivalent.",
            RiskLevel = 2,
            RequiresArchitecturalReview = false,
            PostgresDocReference = "https://www.postgresql.org/docs/current/plpgsql-errors-and-messages.html"
        },

        ["THROW"] = new RemediationEntry
        {
            PostgresEquivalent = "RAISE EXCEPTION 'message' USING ERRCODE = 'xxxxx'",
            RemediationSteps = "Replace THROW error_number, 'message', state with RAISE EXCEPTION 'message' USING ERRCODE = 'P0001'. For re-throwing (THROW without arguments in CATCH block), use RAISE within an EXCEPTION handler. Map SQL Server error numbers to PostgreSQL SQLSTATE codes.",
            IncompatibilityExplanation = "SQL Server's THROW (2012+) raises an error with a number, message, and state. PostgreSQL uses RAISE EXCEPTION with SQLSTATE codes. Error numbers don't map directly between platforms.",
            RiskLevel = 2,
            RequiresArchitecturalReview = false,
            PostgresDocReference = "https://www.postgresql.org/docs/current/plpgsql-errors-and-messages.html"
        },

        ["IMPLICIT_CONVERSION"] = new RemediationEntry
        {
            PostgresEquivalent = "Explicit CAST(value AS type)",
            RemediationSteps = "Add explicit CAST() or :: type casts where SQL Server performs implicit conversion. Common cases: WHERE int_column = '123' → WHERE int_column = CAST('123' AS integer). PostgreSQL is stricter about implicit conversions and may throw errors where SQL Server silently converts.",
            IncompatibilityExplanation = "SQL Server performs many implicit type conversions automatically (e.g., comparing INT to VARCHAR). PostgreSQL requires explicit casts in most cases and will raise type mismatch errors rather than silently converting.",
            RiskLevel = 2,
            RequiresArchitecturalReview = false,
            PostgresDocReference = "https://www.postgresql.org/docs/current/sql-expressions.html#SQL-SYNTAX-TYPE-CASTS"
        },

        ["STRING_SPLIT"] = new RemediationEntry
        {
            PostgresEquivalent = "string_to_table() or regexp_split_to_table()",
            RemediationSteps = "Replace STRING_SPLIT(string, separator) with string_to_table(string, separator) (PostgreSQL 14+) or regexp_split_to_table(string, separator) for older versions. The column name changes from 'value' to the function result. For ordinal support (STRING_SPLIT with enable_ordinal), use WITH ORDINALITY.",
            IncompatibilityExplanation = "STRING_SPLIT is SQL Server 2016+. PostgreSQL provides string_to_table() (v14+) and regexp_split_to_table() with equivalent functionality. Column naming and ordinal support differ slightly.",
            RiskLevel = 2,
            RequiresArchitecturalReview = false,
            PostgresDocReference = "https://www.postgresql.org/docs/current/functions-string.html"
        },

        // ═══════════════════════════════════════════════════════════════
        // Risk 3: Step-by-step conversion instructions
        // ═══════════════════════════════════════════════════════════════

        ["TRY_CATCH"] = new RemediationEntry
        {
            PostgresEquivalent = "BEGIN ... EXCEPTION WHEN ... THEN ... END blocks in PL/pgSQL",
            RemediationSteps = """
                1. Wrap the TRY block contents inside a BEGIN ... EXCEPTION block in PL/pgSQL
                2. Map SQL Server error numbers to PostgreSQL SQLSTATE codes or condition names
                3. Replace CATCH block with EXCEPTION WHEN handlers for specific conditions
                4. Replace ERROR_MESSAGE() with SQLERRM, ERROR_NUMBER() with SQLSTATE
                5. Replace RAISERROR/THROW with RAISE EXCEPTION 'message'
                6. Note: PostgreSQL exception blocks create a subtransaction (savepoint) implicitly
                """,
            IncompatibilityExplanation = "SQL Server's TRY...CATCH is a procedural error handling construct. PostgreSQL uses BEGIN...EXCEPTION...END blocks within PL/pgSQL functions. Error codes and system functions differ between platforms.",
            RiskLevel = 3,
            RequiresArchitecturalReview = false,
            PostgresDocReference = "https://www.postgresql.org/docs/current/plpgsql-control-structures.html#PLPGSQL-ERROR-TRAPPING"
        },

        ["DYNAMIC_SQL"] = new RemediationEntry
        {
            PostgresEquivalent = "EXECUTE format() in PL/pgSQL",
            RemediationSteps = """
                1. Replace EXEC(@sql) or sp_executesql with EXECUTE in PL/pgSQL
                2. Use format() function with %I for identifiers and %L for literals to prevent SQL injection
                3. Replace sp_executesql parameter binding with USING clause: EXECUTE format('SELECT %I FROM %I', col, tbl) USING param1
                4. For dynamic cursors, use OPEN cursor FOR EXECUTE format(...)
                5. Ensure the code is within a PL/pgSQL function or DO block (EXECUTE is not available in plain SQL)
                """,
            IncompatibilityExplanation = "SQL Server uses EXEC/sp_executesql for dynamic SQL which can run in any context. PostgreSQL's EXECUTE is only available within PL/pgSQL. The format() function provides safe identifier/literal quoting.",
            RiskLevel = 3,
            RequiresArchitecturalReview = false,
            PostgresDocReference = "https://www.postgresql.org/docs/current/plpgsql-statements.html#PLPGSQL-STATEMENTS-EXECUTING-DYN"
        },

        ["RAISERROR"] = new RemediationEntry
        {
            PostgresEquivalent = "RAISE EXCEPTION 'format' USING ERRCODE = 'xxxxx'",
            RemediationSteps = """
                1. Replace RAISERROR('message %s', severity, state, param) with RAISE EXCEPTION 'message %', param
                2. Map severity levels: severity >= 16 → RAISE EXCEPTION, severity 10-15 → RAISE WARNING, severity < 10 → RAISE NOTICE
                3. Replace %s/%d format specifiers with PostgreSQL % placeholders
                4. Map SQL Server error state to PostgreSQL SQLSTATE codes using ERRCODE
                5. For WITH NOWAIT behavior, PostgreSQL RAISE is always immediate (no buffering)
                6. Remove WITH LOG option (use PostgreSQL logging configuration instead)
                """,
            IncompatibilityExplanation = "RAISERROR uses C-style format strings with severity/state parameters. PostgreSQL RAISE uses %-based formatting with named severity levels (EXCEPTION, WARNING, NOTICE) and SQLSTATE error codes.",
            RiskLevel = 3,
            RequiresArchitecturalReview = false,
            PostgresDocReference = "https://www.postgresql.org/docs/current/plpgsql-errors-and-messages.html"
        },

        ["TEMP_TABLE"] = new RemediationEntry
        {
            PostgresEquivalent = "CREATE TEMPORARY TABLE (session-scoped by default)",
            RemediationSteps = """
                1. Replace #tableName with CREATE TEMPORARY TABLE tableName (no # prefix needed)
                2. Temporary tables in PostgreSQL are session-scoped by default (dropped at session end)
                3. Use ON COMMIT DROP or ON COMMIT DELETE ROWS for transaction-scoped behavior
                4. Remove any explicit DROP TABLE #tableName at procedure end (optional in PostgreSQL)
                5. Be aware: PostgreSQL temp tables are not visible across sessions (unlike global temp tables)
                6. Consider using CTEs (WITH clause) for simple intermediate results instead of temp tables
                """,
            IncompatibilityExplanation = "SQL Server local temp tables (#name) are connection-scoped and auto-cleaned. PostgreSQL temporary tables are similar but session-scoped by default. The # prefix is not used. Naming conflicts can arise if multiple calls create same-named temp tables.",
            RiskLevel = 3,
            RequiresArchitecturalReview = false,
            PostgresDocReference = "https://www.postgresql.org/docs/current/sql-createtable.html"
        },

        ["OUTPUT"] = new RemediationEntry
        {
            PostgresEquivalent = "RETURNING clause",
            RemediationSteps = """
                1. Replace OUTPUT INSERTED.* with RETURNING * on INSERT statements
                2. Replace OUTPUT DELETED.* with RETURNING * on DELETE statements
                3. For UPDATE, replace OUTPUT INSERTED.col with RETURNING col (returns new values)
                4. For OUTPUT DELETED.col in UPDATE (old values), use a CTE: WITH old AS (UPDATE ... RETURNING *) SELECT ...
                5. Note: RETURNING is available on INSERT, UPDATE, DELETE, and MERGE (PostgreSQL 17+)
                """,
            IncompatibilityExplanation = "SQL Server's OUTPUT clause can return both INSERTED and DELETED pseudo-tables. PostgreSQL's RETURNING clause returns the final state of affected rows. Getting old values from UPDATE requires a CTE workaround.",
            RiskLevel = 3,
            RequiresArchitecturalReview = false,
            PostgresDocReference = "https://www.postgresql.org/docs/current/dml-returning.html"
        },

        ["CROSS_APPLY"] = new RemediationEntry
        {
            PostgresEquivalent = "LATERAL JOIN (CROSS JOIN LATERAL or JOIN ... ON true)",
            RemediationSteps = """
                1. Replace CROSS APPLY subquery with CROSS JOIN LATERAL (subquery)
                2. Replace CROSS APPLY table_function(args) with CROSS JOIN LATERAL table_function(args)
                3. Ensure the lateral subquery references columns from the preceding table (this is what LATERAL enables)
                4. If the subquery is a simple unnest, use CROSS JOIN LATERAL unnest(array_col)
                5. Verify that the lateral subquery filters are preserved correctly
                """,
            IncompatibilityExplanation = "CROSS APPLY is SQL Server syntax for lateral joins where the right-hand side can reference columns from the left. PostgreSQL uses the ANSI-standard LATERAL keyword to achieve the same result.",
            RiskLevel = 3,
            RequiresArchitecturalReview = false,
            PostgresDocReference = "https://www.postgresql.org/docs/current/queries-table-expressions.html#QUERIES-LATERAL"
        },

        ["OUTER_APPLY"] = new RemediationEntry
        {
            PostgresEquivalent = "LEFT JOIN LATERAL ... ON true",
            RemediationSteps = """
                1. Replace OUTER APPLY subquery with LEFT JOIN LATERAL (subquery) ON true
                2. The ON true condition ensures all rows from the left table are preserved (like OUTER APPLY)
                3. If the subquery returns no rows for a left row, NULL values are returned (same as OUTER APPLY)
                4. For table-valued functions: LEFT JOIN LATERAL function_name(args) ON true
                5. Ensure no implicit filtering is introduced by the join condition
                """,
            IncompatibilityExplanation = "OUTER APPLY is SQL Server syntax equivalent to a left lateral join. It preserves all rows from the left table even when the right-side subquery returns no rows. PostgreSQL uses LEFT JOIN LATERAL with ON true.",
            RiskLevel = 3,
            RequiresArchitecturalReview = false,
            PostgresDocReference = "https://www.postgresql.org/docs/current/queries-table-expressions.html#QUERIES-LATERAL"
        },

        // ═══════════════════════════════════════════════════════════════
        // Risk 4: Design pattern recommendations
        // ═══════════════════════════════════════════════════════════════

        ["MERGE"] = new RemediationEntry
        {
            PostgresEquivalent = "INSERT ... ON CONFLICT (UPSERT) or MERGE (PostgreSQL 15+)",
            RemediationSteps = """
                1. For simple upsert (INSERT or UPDATE): Use INSERT ... ON CONFLICT (key) DO UPDATE SET ...
                2. For MERGE with DELETE: Use PostgreSQL 15+ MERGE statement or split into separate INSERT/UPDATE/DELETE with CTEs
                3. Map WHEN MATCHED THEN UPDATE to ON CONFLICT DO UPDATE
                4. Map WHEN NOT MATCHED THEN INSERT to the base INSERT
                5. Map WHEN MATCHED AND condition THEN DELETE requires CTE approach or MERGE (PG 15+)
                6. Ensure conflict target columns match the MERGE join condition
                7. Consider using advisory locks for high-concurrency upsert scenarios
                """,
            IncompatibilityExplanation = "SQL Server MERGE supports INSERT/UPDATE/DELETE in one atomic statement. PostgreSQL's INSERT ON CONFLICT handles the common upsert case. Full MERGE support is available in PostgreSQL 15+, but older versions require CTE-based workarounds.",
            RiskLevel = 4,
            RequiresArchitecturalReview = false,
            PostgresDocReference = "https://www.postgresql.org/docs/current/sql-insert.html#SQL-ON-CONFLICT"
        },

        ["TABLE_VALUED_PARAMETER"] = new RemediationEntry
        {
            PostgresEquivalent = "unnest() with array parameters or temporary tables",
            RemediationSteps = """
                1. Replace table-valued parameter with array parameter: CREATE FUNCTION f(p_ids int[])
                2. Use unnest(p_ids) to expand the array into rows within the function body
                3. For complex TVPs (multiple columns), pass parallel arrays and use unnest(arr1, arr2, ...)
                4. Alternative: Use JSON/JSONB parameter with jsonb_to_recordset() for complex structures
                5. Alternative: Use a temporary table populated by the caller before function invocation
                6. Update application code to pass arrays instead of DataTable/TVP objects
                """,
            IncompatibilityExplanation = "SQL Server table-valued parameters allow passing structured tabular data to procedures. PostgreSQL has no direct equivalent but provides arrays with unnest(), JSON parameters, or temp tables as alternatives.",
            RiskLevel = 4,
            RequiresArchitecturalReview = false,
            PostgresDocReference = "https://www.postgresql.org/docs/current/functions-array.html"
        },

        ["GLOBAL_TEMP_TABLE"] = new RemediationEntry
        {
            PostgresEquivalent = "Unlogged tables or application-managed shared state",
            RemediationSteps = """
                1. Analyze the purpose of the global temp table: Is it for cross-session data sharing or performance?
                2. For cross-session sharing: Replace with an unlogged permanent table with session-aware cleanup
                3. Add a session_id column and cleanup trigger/scheduled job to simulate auto-cleanup
                4. For performance (avoiding WAL): Use UNLOGGED tables (data lost on crash but faster writes)
                5. For application state: Consider moving to application-level caching (Redis, etc.)
                6. Implement explicit lifecycle management since PostgreSQL has no auto-drop for permanent tables
                7. Consider using pg_temp schema references if session isolation is acceptable
                """,
            IncompatibilityExplanation = "SQL Server global temp tables (##name) are visible across all sessions and auto-dropped when the last referencing session disconnects. PostgreSQL has no equivalent construct. Architectural redesign is needed based on the sharing requirements.",
            RiskLevel = 4,
            RequiresArchitecturalReview = true,
            PostgresDocReference = "https://www.postgresql.org/docs/current/sql-createtable.html#SQL-CREATETABLE-UNLOGGED"
        },

        ["NOLOCK"] = new RemediationEntry
        {
            PostgresEquivalent = "PostgreSQL MVCC (no locking hints needed)",
            RemediationSteps = """
                1. Remove WITH (NOLOCK) / WITH (ROWLOCK) / WITH (UPDLOCK) hints entirely
                2. PostgreSQL uses MVCC: readers never block writers and writers never block readers
                3. For NOLOCK (dirty read) behavior: No action needed; PostgreSQL's default READ COMMITTED provides consistent reads without blocking
                4. For UPDLOCK (pessimistic locking): Use SELECT ... FOR UPDATE
                5. For ROWLOCK: Not applicable; PostgreSQL always locks at row level
                6. For TABLOCK/TABLOCKX: Use LOCK TABLE ... IN EXCLUSIVE MODE (rare)
                7. Adjust transaction isolation level if needed: SET TRANSACTION ISOLATION LEVEL SERIALIZABLE
                """,
            IncompatibilityExplanation = "SQL Server uses lock hints to control concurrency behavior. PostgreSQL's MVCC architecture provides non-blocking reads by default. Most lock hints can simply be removed. SELECT FOR UPDATE replaces pessimistic locking needs.",
            RiskLevel = 4,
            RequiresArchitecturalReview = false,
            PostgresDocReference = "https://www.postgresql.org/docs/current/mvcc.html"
        },

        ["ROWLOCK"] = new RemediationEntry
        {
            PostgresEquivalent = "PostgreSQL MVCC (row-level locking is default)",
            RemediationSteps = """
                1. Remove WITH (ROWLOCK) hints entirely
                2. PostgreSQL always uses row-level locking for DML operations by default
                3. No configuration or hints are needed to achieve row-level granularity
                4. For explicit row locking, use SELECT ... FOR UPDATE or FOR SHARE
                """,
            IncompatibilityExplanation = "SQL Server's ROWLOCK hint forces row-level lock granularity. PostgreSQL always locks at row level for DML operations — this is inherent to its MVCC design and cannot be changed to page or table granularity.",
            RiskLevel = 4,
            RequiresArchitecturalReview = false,
            PostgresDocReference = "https://www.postgresql.org/docs/current/explicit-locking.html"
        },

        ["UPDLOCK"] = new RemediationEntry
        {
            PostgresEquivalent = "SELECT ... FOR UPDATE",
            RemediationSteps = """
                1. Replace SELECT ... WITH (UPDLOCK) with SELECT ... FOR UPDATE
                2. For UPDLOCK with ROWLOCK: SELECT ... FOR UPDATE (same behavior)
                3. For read-then-update patterns, ensure FOR UPDATE is on the initial SELECT
                4. Use FOR NO KEY UPDATE if you only update non-key columns (less restrictive)
                5. Add NOWAIT or SKIP LOCKED if non-blocking behavior is needed
                """,
            IncompatibilityExplanation = "SQL Server's UPDLOCK hint acquires update locks to prevent deadlocks in read-then-update patterns. PostgreSQL uses SELECT FOR UPDATE to achieve the same pessimistic locking pattern.",
            RiskLevel = 4,
            RequiresArchitecturalReview = false,
            PostgresDocReference = "https://www.postgresql.org/docs/current/explicit-locking.html#LOCKING-ROWS"
        },

        ["PIVOT"] = new RemediationEntry
        {
            PostgresEquivalent = "crosstab() from tablefunc extension or conditional aggregation with FILTER",
            RemediationSteps = """
                1. Install tablefunc extension: CREATE EXTENSION IF NOT EXISTS tablefunc
                2. Option A (crosstab): SELECT * FROM crosstab('SELECT row_id, category, value FROM source ORDER BY 1,2', 'SELECT DISTINCT category FROM source ORDER BY 1') AS ct(row_id int, cat1 text, cat2 text, ...)
                3. Option B (conditional aggregation): SELECT row_id, SUM(value) FILTER (WHERE category = 'cat1') AS cat1, SUM(value) FILTER (WHERE category = 'cat2') AS cat2 FROM source GROUP BY row_id
                4. For dynamic pivots, generate SQL dynamically using format() in PL/pgSQL
                5. Note: Static column lists must be known at query time (no dynamic column generation in plain SQL)
                """,
            IncompatibilityExplanation = "SQL Server's PIVOT operator provides declarative row-to-column transformation. PostgreSQL requires either the crosstab() function (from tablefunc extension) or manual conditional aggregation with FILTER clauses.",
            RiskLevel = 4,
            RequiresArchitecturalReview = false,
            PostgresDocReference = "https://www.postgresql.org/docs/current/tablefunc.html"
        },

        ["UNPIVOT"] = new RemediationEntry
        {
            PostgresEquivalent = "UNNEST with VALUES or LATERAL join",
            RemediationSteps = """
                1. Option A (VALUES with LATERAL): SELECT id, col_name, col_value FROM source CROSS JOIN LATERAL (VALUES ('col1', col1), ('col2', col2), ('col3', col3)) AS unpivoted(col_name, col_value) WHERE col_value IS NOT NULL
                2. Option B (UNNEST with arrays): SELECT id, unnest(ARRAY['col1','col2','col3']) AS col_name, unnest(ARRAY[col1, col2, col3]) AS col_value FROM source
                3. Filter out NULL values explicitly (UNPIVOT in SQL Server excludes NULLs by default)
                4. Ensure column types are compatible when combining into a single value column
                """,
            IncompatibilityExplanation = "SQL Server's UNPIVOT operator transforms columns into rows. PostgreSQL uses LATERAL joins with VALUES lists or UNNEST with arrays to achieve the same column-to-row transformation.",
            RiskLevel = 4,
            RequiresArchitecturalReview = false,
            PostgresDocReference = "https://www.postgresql.org/docs/current/queries-table-expressions.html#QUERIES-LATERAL"
        },

        ["OPENJSON"] = new RemediationEntry
        {
            PostgresEquivalent = "jsonb_each() / jsonb_array_elements() / jsonb_to_recordset()",
            RemediationSteps = """
                1. Replace OPENJSON(json_string) with jsonb_each(json_string::jsonb) for key-value pairs
                2. Replace OPENJSON(json_array) for arrays with jsonb_array_elements(json_array::jsonb)
                3. For OPENJSON with schema (WITH clause): use jsonb_to_recordset(json::jsonb) AS (col1 type1, col2 type2, ...)
                4. Replace $.path expressions with -> and ->> operators for value extraction
                5. Map SQL Server JSON types to PostgreSQL jsonb operators: $.key → ->>'key'
                6. For nested JSON paths, chain -> operators: $[0].name → ->0->>'name'
                7. Consider using jsonb_path_query() for complex JSONPath expressions (PostgreSQL 12+)
                """,
            IncompatibilityExplanation = "SQL Server's OPENJSON parses JSON text and returns a rowset. PostgreSQL provides jsonb_each(), jsonb_array_elements(), and jsonb_to_recordset() which require explicit schema definitions via AS clause rather than WITH clause.",
            RiskLevel = 4,
            RequiresArchitecturalReview = false,
            PostgresDocReference = "https://www.postgresql.org/docs/current/functions-json.html"
        },

        ["FOR_XML"] = new RemediationEntry
        {
            PostgresEquivalent = "json_agg() / xmlagg(xmlelement()) / string_agg()",
            RemediationSteps = """
                1. FOR XML PATH('') with string concatenation: Replace with string_agg(column, separator)
                2. FOR XML PATH('element'): Replace with json_agg(row_to_json(t)) or xmlagg(xmlelement(name element, ...))
                3. FOR XML AUTO: Replace with json_agg() for JSON output or xmlagg() for XML output
                4. FOR XML RAW: Replace with xmlagg(xmlelement(name row, xmlattributes(col1, col2, ...)))
                5. FOR XML PATH with ROOT: Wrap json_agg() result in json_build_object('root', ...)
                6. For the common comma-separated list pattern (STUFF + FOR XML PATH): Use string_agg(column, ',')
                7. Consider migrating XML output to JSON (json_agg/jsonb_agg) unless XML format is required by consumers
                """,
            IncompatibilityExplanation = "SQL Server's FOR XML converts rowsets to XML. PostgreSQL has no single FOR XML equivalent. The common STUFF/FOR XML PATH concatenation pattern maps to string_agg(). Structured XML output requires xmlagg() with xmlelement(). JSON alternatives (json_agg) are often preferred in PostgreSQL.",
            RiskLevel = 4,
            RequiresArchitecturalReview = false,
            PostgresDocReference = "https://www.postgresql.org/docs/current/functions-json.html"
        },

        // ═══════════════════════════════════════════════════════════════
        // Risk 5: Migration strategies (require architectural review)
        // ═══════════════════════════════════════════════════════════════

        ["SQL_CLR"] = new RemediationEntry
        {
            PostgresEquivalent = "PL/pgSQL functions + PostgreSQL extensions or external microservices",
            RemediationSteps = """
                1. Inventory all CLR functions/procedures and classify by complexity
                2. Simple string/math operations: Rewrite as PL/pgSQL functions
                3. Complex logic with .NET dependencies: Extract into an external microservice callable via pg_net or application layer
                4. File system access: Move to application tier or use external process with pg_notify
                5. Consider PL/Python, PL/Perl, or PL/Java extensions for complex procedural logic
                6. For regex/XML: PostgreSQL has native support that may replace CLR string processing
                7. Plan for performance testing as interpreted PL/pgSQL may be slower than compiled CLR
                8. Consider pgx (Rust extensions) for performance-critical paths
                """,
            IncompatibilityExplanation = "SQL Server CLR integration allows running .NET code inside the database engine. PostgreSQL has no CLR equivalent. Logic must be rewritten in PL/pgSQL, moved to extensions, or extracted to application-tier microservices.",
            RiskLevel = 5,
            RequiresArchitecturalReview = true,
            PostgresDocReference = "https://www.postgresql.org/docs/current/plpgsql.html"
        },

        ["SERVICE_BROKER"] = new RemediationEntry
        {
            PostgresEquivalent = "pgq, pg_notify/LISTEN, or external message queue (RabbitMQ/Kafka)",
            RemediationSteps = """
                1. Map Service Broker usage patterns: async messaging, queue processing, or distributed transactions
                2. For simple pub/sub notifications: Use LISTEN/NOTIFY (pg_notify) for lightweight signaling
                3. For durable queuing: Implement with pgq extension or dedicated queue tables with SKIP LOCKED
                4. For enterprise messaging: Migrate to external message broker (RabbitMQ, Apache Kafka, AWS SQS)
                5. For conversation-based patterns: Redesign as stateless request/response or saga pattern
                6. Remove Service Broker DDL (CREATE SERVICE, CREATE QUEUE, CREATE CONTRACT)
                7. Update application code to use chosen messaging infrastructure
                8. Plan for message ordering and exactly-once delivery guarantees in new architecture
                """,
            IncompatibilityExplanation = "SQL Server Service Broker provides integrated asynchronous messaging within the database. PostgreSQL has no built-in equivalent. Simple cases use LISTEN/NOTIFY; complex scenarios require external message brokers or queue implementations.",
            RiskLevel = 5,
            RequiresArchitecturalReview = true,
            PostgresDocReference = "https://www.postgresql.org/docs/current/sql-notify.html"
        },

        ["LINKED_SERVER"] = new RemediationEntry
        {
            PostgresEquivalent = "postgres_fdw or dblink extension",
            RemediationSteps = """
                1. Install postgres_fdw: CREATE EXTENSION postgres_fdw
                2. Create foreign server: CREATE SERVER remote_server FOREIGN DATA WRAPPER postgres_fdw OPTIONS (host '...', dbname '...', port '...')
                3. Create user mapping: CREATE USER MAPPING FOR local_user SERVER remote_server OPTIONS (user '...', password '...')
                4. Import or create foreign tables: IMPORT FOREIGN SCHEMA public FROM SERVER remote_server INTO local_schema
                5. Replace four-part names (server.db.schema.table) with foreign table references
                6. For non-PostgreSQL remote sources: Use appropriate FDW (mysql_fdw, oracle_fdw, tds_fdw)
                7. For ad-hoc queries: Use dblink() function as a simpler alternative
                8. Consider network latency and plan for query performance across foreign tables
                """,
            IncompatibilityExplanation = "SQL Server Linked Servers provide transparent access to remote databases using four-part naming. PostgreSQL uses Foreign Data Wrappers (FDW) which provide similar functionality but with different setup and query patterns.",
            RiskLevel = 5,
            RequiresArchitecturalReview = true,
            PostgresDocReference = "https://www.postgresql.org/docs/current/postgres-fdw.html"
        },

        ["XML_METHOD"] = new RemediationEntry
        {
            PostgresEquivalent = "xpath(), xmltable(), xmlparse()",
            RemediationSteps = """
                1. Replace .value('xpath', 'type') with (xpath('xpath', xml_col))[1]::type
                2. Replace .query('xpath') with xpath('xpath', xml_col)
                3. Replace .nodes('xpath') with xmltable('xpath' PASSING xml_col COLUMNS ...)
                4. Replace .modify() with xmlelement/xmlforest for construction or XSLT for transforms
                5. Replace .exist('xpath') with (xpath_exists('xpath', xml_col))
                6. Replace FOR XML PATH with xmlagg(xmlelement(...)) or string_agg for simple concatenation
                7. For complex XML processing, consider migrating to JSON/JSONB which has better PostgreSQL support
                8. Test xpath namespace handling as syntax differs between platforms
                """,
            IncompatibilityExplanation = "SQL Server XML methods (.value, .query, .nodes, .modify, .exist) use XQuery syntax integrated into T-SQL. PostgreSQL uses xpath() and xmltable() functions with standard XPath. FOR XML has no direct equivalent.",
            RiskLevel = 5,
            RequiresArchitecturalReview = true,
            PostgresDocReference = "https://www.postgresql.org/docs/current/functions-xml.html"
        },

        ["OPENQUERY"] = new RemediationEntry
        {
            PostgresEquivalent = "postgres_fdw with foreign tables",
            RemediationSteps = """
                1. Set up postgres_fdw or appropriate FDW for the remote data source
                2. Create foreign server and user mapping (see LINKED_SERVER guidance)
                3. Replace OPENQUERY(server, 'query') with direct SELECT against foreign tables
                4. For pass-through queries that must execute remotely: Use dblink('connstr', 'query')
                5. For performance: Create materialized views over foreign tables for frequently accessed data
                6. Ensure network connectivity and firewall rules allow PostgreSQL to reach remote servers
                """,
            IncompatibilityExplanation = "OPENQUERY executes pass-through queries on linked servers. PostgreSQL uses foreign data wrappers for transparent remote access or dblink for ad-hoc remote queries. The approach depends on query frequency and complexity.",
            RiskLevel = 5,
            RequiresArchitecturalReview = true,
            PostgresDocReference = "https://www.postgresql.org/docs/current/postgres-fdw.html"
        },

        ["OPENROWSET"] = new RemediationEntry
        {
            PostgresEquivalent = "file_fdw, COPY command, or pg_read_file()",
            RemediationSteps = """
                1. For file access (CSV, Excel): Use file_fdw extension to create foreign tables over files
                2. Install file_fdw: CREATE EXTENSION file_fdw; CREATE SERVER file_server FOREIGN DATA WRAPPER file_fdw
                3. Create foreign table mapping file columns: CREATE FOREIGN TABLE ... SERVER file_server OPTIONS (filename '...', format 'csv')
                4. For bulk data import: Use COPY ... FROM '/path/file.csv' WITH (FORMAT csv, HEADER)
                5. For ad-hoc remote database access: Use dblink or postgres_fdw (see LINKED_SERVER guidance)
                6. For programmatic file reading: Use pg_read_file() for text files (superuser only)
                7. Move file processing to application tier where possible for better security
                """,
            IncompatibilityExplanation = "OPENROWSET provides ad-hoc access to remote data sources and files without linked server setup. PostgreSQL uses file_fdw for file access and COPY for bulk import. Remote database access uses FDW or dblink.",
            RiskLevel = 5,
            RequiresArchitecturalReview = true,
            PostgresDocReference = "https://www.postgresql.org/docs/current/file-fdw.html"
        },

        ["FILESTREAM"] = new RemediationEntry
        {
            PostgresEquivalent = "Large Objects (lo), bytea, or external storage (S3/blob storage)",
            RemediationSteps = """
                1. Assess file sizes and access patterns to choose the right approach
                2. For files < 1GB: Use bytea columns for simplicity (stored inline in table)
                3. For files > 1GB or streaming access: Use Large Objects (lo_create, lo_open, lo_read, lo_write)
                4. For very large files or high throughput: Move to external storage (S3, Azure Blob, MinIO)
                5. Store external storage references (URLs/keys) in PostgreSQL columns
                6. Replace FILESTREAM-specific T-SQL (PathName(), GET_FILESTREAM_TRANSACTION_CONTEXT) with lo_* functions or application-tier storage API calls
                7. Plan data migration: Export FILESTREAM data and import into chosen storage
                8. Update application code for new file access patterns
                """,
            IncompatibilityExplanation = "SQL Server FILESTREAM stores large binary data in the file system with transactional consistency. PostgreSQL offers Large Objects for similar in-database binary storage, or the common modern pattern of external object storage with database references.",
            RiskLevel = 5,
            RequiresArchitecturalReview = true,
            PostgresDocReference = "https://www.postgresql.org/docs/current/largeobjects.html"
        },

        ["MEMORY_OPTIMIZED"] = new RemediationEntry
        {
            PostgresEquivalent = "Standard PostgreSQL tables (already MVCC-based, no special construct needed)",
            RemediationSteps = """
                1. Remove MEMORY_OPTIMIZED = ON from table definitions
                2. Remove DURABILITY = SCHEMA_ONLY or SCHEMA_AND_DATA options
                3. Replace natively compiled stored procedures with standard PL/pgSQL functions
                4. Remove WITH (MEMORY_OPTIMIZED = ON) from table type definitions
                5. For SCHEMA_ONLY durability (non-durable): Consider UNLOGGED tables for similar performance
                6. For extremely high-throughput scenarios: Tune shared_buffers, use connection pooling
                7. PostgreSQL's MVCC already provides non-blocking reads similar to memory-optimized tables
                8. Profile and benchmark after migration — PostgreSQL's buffer cache often provides comparable performance
                """,
            IncompatibilityExplanation = "SQL Server Memory-Optimized Tables (In-Memory OLTP / Hekaton) use lock-free, latch-free data structures in memory. PostgreSQL tables are already MVCC-based with efficient buffer caching. Most workloads perform comparably without special memory-optimized constructs.",
            RiskLevel = 5,
            RequiresArchitecturalReview = true,
            PostgresDocReference = "https://www.postgresql.org/docs/current/runtime-config-resource.html#GUC-SHARED-BUFFERS"
        }
    };

    /// <inheritdoc />
    public RemediationEntry? GetGuidance(string featureName)
    {
        return Entries.TryGetValue(featureName, out var entry) ? entry : null;
    }

    /// <inheritdoc />
    public bool HasGuidance(string featureName)
    {
        return Entries.ContainsKey(featureName);
    }
}
