namespace MigrationAssessment.Core.Models;

/// <summary>
/// Complete inventory of database objects collected from metadata queries.
/// </summary>
public sealed record DatabaseObjectInventory
{
    public required IReadOnlyList<TableMetadata> Tables { get; init; }
    public required IReadOnlyList<IndexMetadata> Indexes { get; init; }
    public required IReadOnlyList<ConstraintMetadata> Constraints { get; init; }
    public required IReadOnlyList<ForeignKeyMetadata> ForeignKeys { get; init; }
    public required IReadOnlyList<ProgrammableObjectMetadata> ProgrammableObjects { get; init; }
    public required IReadOnlyList<SynonymMetadata> Synonyms { get; init; }
}
