using MigrationAssessment.Core.Models;

namespace MigrationAssessment.WorkItems.Models;

/// <summary>
/// Result of deduplication for a single statement group, containing the assigned ID,
/// primary SQL example, cross-references, and affected object details.
/// </summary>
public sealed record DeduplicatedGroup
{
    /// <summary>The original statement group.</summary>
    public required StatementGroup Group { get; init; }

    /// <summary>Assigned unique identifier in format "WI-001".</summary>
    public required string Id { get; init; }

    /// <summary>
    /// The SQL text from the highest-WeightedRisk statement, truncated to 500 characters.
    /// Used as the primary example in the SQL Server pattern section.
    /// </summary>
    public required string PrimarySqlPattern { get; init; }

    /// <summary>
    /// The primary statement (highest WeightedRisk) selected as the representative example.
    /// </summary>
    public required AnalyzedStatement PrimaryStatement { get; init; }

    /// <summary>
    /// Combined priority score: sum of WeightedRisk across all statements in the group,
    /// incorporating execution frequencies.
    /// </summary>
    public required double CombinedPriorityScore { get; init; }

    /// <summary>
    /// IDs of related work items that share the same database object.
    /// Empty if the database object appears in only one work item.
    /// </summary>
    public required IReadOnlyList<string> RelatedWorkItemIds { get; init; }

    /// <summary>
    /// Affected objects with their statement counts.
    /// </summary>
    public required IReadOnlyList<AffectedObject> AffectedObjects { get; init; }
}
