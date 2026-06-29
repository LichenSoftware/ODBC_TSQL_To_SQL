namespace MigrationAssessment.WorkItems.Models;

/// <summary>
/// A single remediation work item ticket.
/// </summary>
public sealed record WorkItem
{
    /// <summary>Unique identifier in format "WI-001".</summary>
    public required string Id { get; init; }

    /// <summary>Title, max 120 chars: "[Risk N] Convert feature_name in object_name".</summary>
    public required string Title { get; init; }

    /// <summary>Plain-language description of the issue and business impact.</summary>
    public required string Description { get; init; }

    /// <summary>Actual SQL excerpt demonstrating the SQL Server construct (max 500 chars).</summary>
    public required string SqlServerPattern { get; init; }

    /// <summary>PostgreSQL equivalent code example.</summary>
    public required string PostgresEquivalent { get; init; }

    /// <summary>List of affected database objects.</summary>
    public required IReadOnlyList<AffectedObject> AffectedObjects { get; init; }

    /// <summary>Risk level 1-5.</summary>
    public required int RiskLevel { get; init; }

    /// <summary>Priority label: Critical, High, Medium, Low.</summary>
    public required string Priority { get; init; }

    /// <summary>Numeric priority score (sum of weighted risks).</summary>
    public required double PriorityScore { get; init; }

    /// <summary>Estimated effort range.</summary>
    public required HourRange EstimatedEffort { get; init; }

    /// <summary>Verifiable acceptance criteria.</summary>
    public required IReadOnlyList<string> AcceptanceCriteria { get; init; }

    /// <summary>Detailed remediation guidance.</summary>
    public required string RemediationGuidance { get; init; }

    /// <summary>Tags for categorization and filtering.</summary>
    public required IReadOnlyList<string> Tags { get; init; }

    /// <summary>Related work item IDs for the same database object.</summary>
    public IReadOnlyList<string> RelatedWorkItemIds { get; init; } = [];

    /// <summary>All feature names detected in this work item's statement(s).</summary>
    public IReadOnlyList<string> DetectedFeatures { get; init; } = [];

    /// <summary>Distinct feature names for filtering (same as DetectedFeatures distinct).</summary>
    public IReadOnlyList<string> RelatedFeatures { get; init; } = [];
}
