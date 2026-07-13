namespace SchemaConversion.RuleEngine.Models;

/// <summary>
/// Result of mapping a SQL Server type to a PostgreSQL type.
/// </summary>
public sealed record TypeMappingResult
{
    /// <summary>
    /// The mapped PostgreSQL type string (e.g., "NUMERIC(18,2)", "SMALLINT").
    /// Null if the type requires manual review and has no direct mapping.
    /// </summary>
    public string? MappedType { get; init; }

    /// <summary>
    /// Optional CHECK constraint text to be added to the column definition.
    /// Contains {column} placeholder that must be replaced with the actual column name.
    /// </summary>
    public string? AdditionalConstraint { get; init; }

    /// <summary>
    /// Whether the mapping requires manual review (e.g., HIERARCHYID, GEOGRAPHY).
    /// </summary>
    public bool RequiresManualReview { get; init; }

    /// <summary>
    /// Compatibility note for types that don't have a direct equivalent.
    /// </summary>
    public string? CompatibilityNote { get; init; }
}
