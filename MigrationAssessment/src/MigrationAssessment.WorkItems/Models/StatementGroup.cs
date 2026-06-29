using MigrationAssessment.Core.Models;

namespace MigrationAssessment.WorkItems.Models;

/// <summary>
/// A group of related statements that will become a single work item.
/// </summary>
public sealed record StatementGroup
{
    /// <summary>Primary feature name (highest risk) — retained for backward compat.</summary>
    public required string FeatureName { get; init; }

    /// <summary>ALL features detected in statements in this group.</summary>
    public required IReadOnlyList<string> DetectedFeatures { get; init; }

    /// <summary>The database object containing these statements (null for ad hoc).</summary>
    public string? DatabaseObjectName { get; init; }

    /// <summary>Type of the database object.</summary>
    public string DatabaseObjectType { get; init; } = "AdHoc";

    /// <summary>All statements in this group.</summary>
    public required IReadOnlyList<AnalyzedStatement> Statements { get; init; }

    /// <summary>Whether this is a server-level feature (from feature inventory).</summary>
    public bool IsServerLevelFeature { get; init; }

    /// <summary>The highest risk level among grouped statements.</summary>
    public required int MaxRiskLevel { get; init; }
}
