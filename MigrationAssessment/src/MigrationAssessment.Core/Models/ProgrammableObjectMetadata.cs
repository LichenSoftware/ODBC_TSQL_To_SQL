namespace MigrationAssessment.Core.Models;

/// <summary>
/// Metadata for a programmable database object (View, Trigger, Function, StoredProcedure).
/// </summary>
public sealed record ProgrammableObjectMetadata
{
    public required string SchemaName { get; init; }
    public required string ObjectName { get; init; }
    public required string ObjectType { get; init; }
    public string? SourceText { get; init; }
    public bool IsEncrypted { get; init; }
    public string? InaccessibilityReason { get; init; }
}
