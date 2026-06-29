namespace MigrationAssessment.WorkItems.Models;

/// <summary>
/// Aggregated effort breakdown by confidence level, providing stakeholders
/// with visibility into estimate reliability.
/// </summary>
public sealed record ConfidenceSummary
{
    /// <summary>Sum of effort for all high-confidence items (risk 1–2, ≤2x range).</summary>
    public required HourRange HighConfidenceHours { get; init; }

    /// <summary>Sum of effort for all medium-confidence items (risk 3, 2–4x range).</summary>
    public required HourRange MediumConfidenceHours { get; init; }

    /// <summary>Sum of effort for all low-confidence items (risk 4–5, 4–7x range).</summary>
    public required HourRange LowConfidenceHours { get; init; }

    /// <summary>
    /// Explanation of what would need to be true to move low-confidence items to medium,
    /// or medium items to high confidence.
    /// </summary>
    public required string Notes { get; init; }
}
