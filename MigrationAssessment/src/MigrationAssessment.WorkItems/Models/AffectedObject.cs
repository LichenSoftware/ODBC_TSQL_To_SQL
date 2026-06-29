namespace MigrationAssessment.WorkItems.Models;

/// <summary>
/// A database object affected by a work item.
/// </summary>
public sealed record AffectedObject
{
    /// <summary>Object name (schema.name or "Ad Hoc Queries").</summary>
    public required string Name { get; init; }

    /// <summary>Object type: StoredProcedure, Function, View, Trigger, AdHoc.</summary>
    public required string Type { get; init; }

    /// <summary>Number of statements within this object referencing the feature.</summary>
    public required int StatementCount { get; init; }
}
