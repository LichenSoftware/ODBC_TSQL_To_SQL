namespace MigrationAssessment.Core.Models;

/// <summary>
/// Metadata for a database synonym pointing to a base object.
/// </summary>
public sealed record SynonymMetadata
{
    public required string SchemaName { get; init; }
    public required string SynonymName { get; init; }
    public required string BaseObjectName { get; init; }
}
