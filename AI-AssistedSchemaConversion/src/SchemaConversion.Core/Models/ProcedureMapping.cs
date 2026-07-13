namespace SchemaConversion.Core.Models;

/// <summary>
/// Represents the complete mapping manifest for PgPassthrough,
/// documenting how T-SQL stored procedure calls translate to PostgreSQL function calls.
/// </summary>
public sealed record ProcedureMappingManifest
{
    public required string SessionId { get; init; }
    public required DateTimeOffset GeneratedAt { get; init; }
    public required string Description { get; init; }
    public required IReadOnlyList<ProcedureMapping> Mappings { get; init; }
    public required MappingSummary Summary { get; init; }
}

public sealed record ProcedureMapping
{
    /// <summary>The original T-SQL qualified name (e.g., "dbo.usp_GetTopCustomers").</summary>
    public required string OriginalName { get; init; }

    /// <summary>The PostgreSQL function name (e.g., "dbo.usp_GetTopCustomers").</summary>
    public required string PostgresName { get; init; }

    /// <summary>The original object type in SQL Server.</summary>
    public required string OriginalType { get; init; }

    /// <summary>The PostgreSQL object type (typically "function").</summary>
    public required string PostgresType { get; init; }

    /// <summary>How the application should call this in PostgreSQL.</summary>
    public required string CallPattern { get; init; }

    /// <summary>The original T-SQL call pattern (e.g., "EXEC dbo.usp_GetTopCustomers @TopN").</summary>
    public required string OriginalCallPattern { get; init; }

    /// <summary>Parameter mapping from T-SQL to PostgreSQL.</summary>
    public required IReadOnlyList<ParameterMapping> Parameters { get; init; }

    /// <summary>What the function returns in PostgreSQL.</summary>
    public required string ReturnType { get; init; }

    /// <summary>Conversion confidence score (0.0–1.0).</summary>
    public double? Confidence { get; init; }

    /// <summary>Conversion status: converted, flagged, failed.</summary>
    public required string Status { get; init; }

    /// <summary>Notable behavioral differences the application team should be aware of.</summary>
    public IReadOnlyList<string> CompatibilityNotes { get; init; } = [];
}

public sealed record ParameterMapping
{
    /// <summary>Original T-SQL parameter name (e.g., "@TopN").</summary>
    public required string Original { get; init; }

    /// <summary>PostgreSQL parameter name (e.g., "p_TopN").</summary>
    public required string Postgres { get; init; }

    /// <summary>Positional index (1-based) for the PostgreSQL function call.</summary>
    public required int Position { get; init; }

    /// <summary>The SQL Server data type.</summary>
    public string? OriginalType { get; init; }

    /// <summary>The PostgreSQL data type.</summary>
    public string? PostgresType { get; init; }
}

public sealed record MappingSummary
{
    public required int TotalMappings { get; init; }
    public required int Converted { get; init; }
    public required int Flagged { get; init; }
    public required int Failed { get; init; }
    public required int NoParameters { get; init; }
    public required int WithParameters { get; init; }
}
