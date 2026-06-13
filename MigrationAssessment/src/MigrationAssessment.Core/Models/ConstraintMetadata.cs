namespace MigrationAssessment.Core.Models;

/// <summary>
/// Metadata for a table constraint (PrimaryKey, Unique, Check, Default).
/// </summary>
public sealed record ConstraintMetadata
{
    public required string SchemaName { get; init; }
    public required string TableName { get; init; }
    public required string ConstraintName { get; init; }
    public required string ConstraintType { get; init; }
    public string? Expression { get; init; }
    public IReadOnlyList<string> Columns { get; init; } = Array.Empty<string>();
}
