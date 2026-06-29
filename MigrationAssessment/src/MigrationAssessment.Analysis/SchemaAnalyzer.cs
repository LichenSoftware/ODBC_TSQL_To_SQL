using MigrationAssessment.Core.Interfaces;
using MigrationAssessment.Core.Models;

namespace MigrationAssessment.Analysis;

/// <summary>
/// Analyzes schema DDL metadata to detect SQL Server-specific patterns
/// that require conversion for PostgreSQL migration. Detects:
/// - Data type mappings (UNIQUEIDENTIFIER, DATETIME, BIT, etc.)
/// - Identity columns (IDENTITY → GENERATED ALWAYS AS IDENTITY)
/// - Clustered indexes (no PostgreSQL equivalent)
/// - Collation issues (case-insensitive defaults)
/// - Computed columns (AS expression → GENERATED ALWAYS AS STORED)
/// </summary>
public sealed class SchemaAnalyzer : ISchemaAnalyzer
{
    /// <summary>
    /// Data type mappings from SQL Server to PostgreSQL with risk scores.
    /// </summary>
    private static readonly Dictionary<string, (string PgType, int Risk, string Desc)> DataTypeMappings =
        new(StringComparer.OrdinalIgnoreCase)
        {
            ["UNIQUEIDENTIFIER"] = ("UUID", 2, "Replace with UUID type; use gen_random_uuid() for defaults"),
            ["DATETIME"] = ("TIMESTAMPTZ", 2, "DATETIME has lower precision; TIMESTAMPTZ includes timezone"),
            ["SMALLDATETIME"] = ("TIMESTAMPTZ", 2, "SMALLDATETIME rounds to minutes; consider precision needs"),
            ["DATETIME2"] = ("TIMESTAMPTZ", 1, "Direct mapping; TIMESTAMPTZ preserves precision"),
            ["BIT"] = ("BOOLEAN", 1, "Direct mapping; BIT(1) → BOOLEAN"),
            ["IMAGE"] = ("BYTEA", 2, "Deprecated type; migrate to BYTEA"),
            ["MONEY"] = ("NUMERIC(19,4)", 2, "MONEY has implicit rounding; NUMERIC is explicit"),
            ["SMALLMONEY"] = ("NUMERIC(10,4)", 2, "SMALLMONEY has implicit rounding; NUMERIC is explicit"),
            ["TINYINT"] = ("SMALLINT", 1, "PostgreSQL has no unsigned byte type; SMALLINT is closest"),
        };

    /// <inheritdoc />
    public SchemaAnalysisResult Analyze(DatabaseObjectInventory objectInventory)
    {
        ArgumentNullException.ThrowIfNull(objectInventory);

        var findings = new List<SchemaFinding>();

        // Analyze tables and columns
        foreach (var table in objectInventory.Tables)
        {
            var qualifiedName = $"{table.SchemaName}.{table.TableName}";
            AnalyzeColumns(qualifiedName, table.Columns, findings);
        }

        // Analyze indexes for CLUSTERED
        AnalyzeIndexes(objectInventory.Indexes, findings);

        // Calculate effort from findings
        var effort = CalculateSchemaEffort(findings);

        // Build summary counts
        var countsByType = findings
            .GroupBy(f => f.IssueType)
            .ToDictionary(g => g.Key, g => g.Count());

        return new SchemaAnalysisResult
        {
            Findings = findings,
            EstimatedEffort = effort,
            FindingCountsByType = countsByType
        };
    }

    private static void AnalyzeColumns(
        string tableName,
        IReadOnlyList<ColumnMetadata> columns,
        List<SchemaFinding> findings)
    {
        foreach (var column in columns)
        {
            // 1. Data type mappings
            CheckDataTypeMapping(tableName, column, findings);

            // 2. Identity columns
            if (column.IsIdentity)
            {
                findings.Add(new SchemaFinding
                {
                    TableName = tableName,
                    ColumnName = column.ColumnName,
                    IssueType = "Identity",
                    SqlServerType = "IDENTITY(seed, increment)",
                    PostgresType = "GENERATED ALWAYS AS IDENTITY",
                    RiskScore = 2,
                    Description = "IDENTITY columns map to GENERATED ALWAYS AS IDENTITY. " +
                                  "Behavior differs on DELETE/reinsert: PostgreSQL does not reuse values."
                });
            }

            // 3. Computed columns
            if (!string.IsNullOrWhiteSpace(column.ComputedDefinition))
            {
                findings.Add(new SchemaFinding
                {
                    TableName = tableName,
                    ColumnName = column.ColumnName,
                    IssueType = "ComputedColumn",
                    SqlServerType = $"AS ({column.ComputedDefinition})",
                    PostgresType = $"GENERATED ALWAYS AS ({column.ComputedDefinition}) STORED",
                    RiskScore = 3,
                    Description = "Computed columns map to GENERATED ALWAYS AS ... STORED. " +
                                  "Expression syntax may require conversion (e.g., ISNULL → COALESCE). " +
                                  "PostgreSQL does not support virtual (non-stored) computed columns."
                });
            }

            // 4. Collation — check for case-insensitive patterns in data type
            CheckCollation(tableName, column, findings);
        }
    }

    private static void CheckDataTypeMapping(
        string tableName,
        ColumnMetadata column,
        List<SchemaFinding> findings)
    {
        var normalizedType = NormalizeDataType(column.DataType, column.MaxLength);

        if (DataTypeMappings.TryGetValue(normalizedType, out var mapping))
        {
            findings.Add(new SchemaFinding
            {
                TableName = tableName,
                ColumnName = column.ColumnName,
                IssueType = "DataType",
                SqlServerType = column.DataType + FormatTypeSize(column),
                PostgresType = mapping.PgType,
                RiskScore = mapping.Risk,
                Description = mapping.Desc
            });
            return;
        }

        // Check for MAX-length string/binary types
        if (IsMaxLengthType(column.DataType, column.MaxLength))
        {
            var pgType = column.DataType.Contains("BINARY", StringComparison.OrdinalIgnoreCase)
                ? "BYTEA"
                : "TEXT";
            var desc = column.DataType.Contains("BINARY", StringComparison.OrdinalIgnoreCase)
                ? "VARBINARY(MAX) maps to BYTEA; consider external storage for very large objects"
                : "VARCHAR(MAX)/NVARCHAR(MAX) maps to TEXT; no length limit in PostgreSQL";

            findings.Add(new SchemaFinding
            {
                TableName = tableName,
                ColumnName = column.ColumnName,
                IssueType = "DataType",
                SqlServerType = $"{column.DataType}(MAX)",
                PostgresType = pgType,
                RiskScore = 2,
                Description = desc
            });
        }
    }

    private static void CheckCollation(
        string tableName,
        ColumnMetadata column,
        List<SchemaFinding> findings)
    {
        // String types that might have case-insensitive collation
        var isStringType = column.DataType.Contains("CHAR", StringComparison.OrdinalIgnoreCase)
                        || column.DataType.Contains("TEXT", StringComparison.OrdinalIgnoreCase);

        if (!isStringType) return;

        // SQL Server default collation is typically case-insensitive (CI).
        // Flag string columns as potentially needing citext or explicit collation.
        // We can only infer this from metadata — if the column has no explicit collation,
        // it inherits the database default which is usually CI_AS.
        findings.Add(new SchemaFinding
        {
            TableName = tableName,
            ColumnName = column.ColumnName,
            IssueType = "Collation",
            SqlServerType = "SQL_Latin1_General_CP1_CI_AS (default)",
            PostgresType = "citext extension or explicit COLLATE \"und-x-icu\"",
            RiskScore = 3,
            Description = "SQL Server defaults to case-insensitive collation. PostgreSQL defaults to " +
                          "case-sensitive. Consider using the citext extension or adding explicit " +
                          "case-insensitive collation for columns used in WHERE/JOIN comparisons."
        });
    }

    private static void AnalyzeIndexes(
        IReadOnlyList<IndexMetadata> indexes,
        List<SchemaFinding> findings)
    {
        foreach (var index in indexes)
        {
            if (index.IndexType.Equals("CLUSTERED", StringComparison.OrdinalIgnoreCase))
            {
                findings.Add(new SchemaFinding
                {
                    TableName = $"{index.SchemaName}.{index.TableName}",
                    ColumnName = index.IndexName,
                    IssueType = "ClusteredIndex",
                    SqlServerType = "CLUSTERED INDEX",
                    PostgresType = "B-tree index (non-clustered) + optional CLUSTER command",
                    RiskScore = 3,
                    Description = "PostgreSQL has no clustered indexes. Data is stored in heap order. " +
                                  "Use CLUSTER command for one-time physical reordering, or consider " +
                                  "index-organized approaches for read-heavy workloads."
                });
            }
        }
    }

    /// <summary>
    /// Calculates schema conversion effort from the count and risk of findings.
    /// Base: 0.25h per Risk 1-2 finding, 0.5h per Risk 3, 1h per Risk 4-5.
    /// </summary>
    private static HourRange CalculateSchemaEffort(List<SchemaFinding> findings)
    {
        if (findings.Count == 0)
        {
            return new HourRange { MinHours = 0, MaxHours = 0 };
        }

        double minTotal = 0;
        double maxTotal = 0;

        foreach (var finding in findings)
        {
            var (min, max) = finding.RiskScore switch
            {
                <= 2 => (0.1, 0.25),
                3 => (0.25, 0.75),
                4 => (0.5, 2.0),
                _ => (1.0, 4.0)
            };
            minTotal += min;
            maxTotal += max;
        }

        return new HourRange
        {
            MinHours = Math.Max(1, (int)Math.Ceiling(minTotal)),
            MaxHours = Math.Max(2, (int)Math.Ceiling(maxTotal))
        };
    }

    // ═══════════════════════════════════════════════════════════════
    // Helper methods
    // ═══════════════════════════════════════════════════════════════

    private static string NormalizeDataType(string dataType, int? maxLength)
    {
        // Strip parenthetical precision/size for lookup
        var parenIndex = dataType.IndexOf('(');
        return parenIndex >= 0 ? dataType[..parenIndex].Trim() : dataType.Trim();
    }

    private static bool IsMaxLengthType(string dataType, int? maxLength)
    {
        // SQL Server uses -1 for MAX in sys.columns
        if (maxLength == -1) return true;

        // Also check if the type string explicitly says MAX
        return dataType.Contains("MAX", StringComparison.OrdinalIgnoreCase);
    }

    private static string FormatTypeSize(ColumnMetadata column)
    {
        if (column.MaxLength == -1)
            return "(MAX)";
        if (column.Precision.HasValue && column.Scale.HasValue && column.Scale.Value > 0)
            return $"({column.Precision},{column.Scale})";
        if (column.MaxLength.HasValue && column.MaxLength.Value > 0)
            return $"({column.MaxLength})";
        return "";
    }
}
