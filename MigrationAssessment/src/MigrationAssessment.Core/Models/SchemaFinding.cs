namespace MigrationAssessment.Core.Models;

/// <summary>
/// A single schema-level finding flagging a column, index, or constraint
/// that requires conversion from SQL Server to PostgreSQL.
/// </summary>
public sealed record SchemaFinding
{
    /// <summary>Schema-qualified table name (e.g., "dbo.Orders").</summary>
    public required string TableName { get; init; }

    /// <summary>Column or index name affected.</summary>
    public required string ColumnName { get; init; }

    /// <summary>Category of the issue (e.g., "DataType", "Identity", "ClusteredIndex", "Collation", "ComputedColumn").</summary>
    public required string IssueType { get; init; }

    /// <summary>Original SQL Server data type or construct.</summary>
    public required string SqlServerType { get; init; }

    /// <summary>Recommended PostgreSQL equivalent.</summary>
    public required string PostgresType { get; init; }

    /// <summary>Risk score 1-5 for this schema finding.</summary>
    public required int RiskScore { get; init; }

    /// <summary>Human-readable description of the conversion concern.</summary>
    public string? Description { get; init; }
}
