namespace MigrationAssessment.Core.Models;

/// <summary>
/// Result of schema DDL analysis containing all flagged columns, indexes,
/// and constraints that require attention during migration.
/// </summary>
public sealed record SchemaAnalysisResult
{
    /// <summary>All schema-level findings requiring conversion.</summary>
    public required IReadOnlyList<SchemaFinding> Findings { get; init; }

    /// <summary>Estimated effort for schema conversion derived from findings.</summary>
    public required HourRange EstimatedEffort { get; init; }

    /// <summary>Summary counts by issue type.</summary>
    public required IReadOnlyDictionary<string, int> FindingCountsByType { get; init; }
}
