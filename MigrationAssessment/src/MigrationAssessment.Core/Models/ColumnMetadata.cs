namespace MigrationAssessment.Core.Models;

/// <summary>
/// Metadata for a table column including type information and constraints.
/// </summary>
public sealed record ColumnMetadata
{
    public required string ColumnName { get; init; }
    public required int OrdinalPosition { get; init; }
    public required string DataType { get; init; }
    public int? Precision { get; init; }
    public int? Scale { get; init; }
    public int? MaxLength { get; init; }
    public required bool IsNullable { get; init; }
    public bool IsIdentity { get; init; }
    public string? ComputedDefinition { get; init; }
}
