namespace MigrationAssessment.Core.Models;

/// <summary>
/// Metadata for a foreign key relationship between tables.
/// </summary>
public sealed record ForeignKeyMetadata
{
    public required string SchemaName { get; init; }
    public required string ConstraintName { get; init; }
    public required string ParentTable { get; init; }
    public required IReadOnlyList<string> ParentColumns { get; init; }
    public required string ReferencedTable { get; init; }
    public required IReadOnlyList<string> ReferencedColumns { get; init; }
    public required string UpdateRule { get; init; }
    public required string DeleteRule { get; init; }
}
