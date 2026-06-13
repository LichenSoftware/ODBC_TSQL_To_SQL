namespace MigrationAssessment.Core.Models;

/// <summary>
/// Metadata for a database table including its columns.
/// </summary>
public sealed record TableMetadata
{
    public required string SchemaName { get; init; }
    public required string TableName { get; init; }
    public required IReadOnlyList<ColumnMetadata> Columns { get; init; }
}
