namespace MigrationAssessment.Core.Models;

/// <summary>
/// Metadata for a database index including key and included columns.
/// </summary>
public sealed record IndexMetadata
{
    public required string SchemaName { get; init; }
    public required string TableName { get; init; }
    public required string IndexName { get; init; }
    public required string IndexType { get; init; }
    public required IReadOnlyList<string> KeyColumns { get; init; }
    public IReadOnlyList<string> IncludedColumns { get; init; } = Array.Empty<string>();
    public string? FilterExpression { get; init; }
    public int? FillFactor { get; init; }
}
