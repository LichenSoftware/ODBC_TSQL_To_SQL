namespace MigrationAssessment.WorkItems.Models;

/// <summary>
/// A range of estimated hours (minimum to maximum) using fractional hours
/// for fine-grained effort estimation (e.g., 0.08 hours = 5 minutes).
/// </summary>
public sealed record HourRange
{
    /// <summary>Minimum estimated hours.</summary>
    public required double MinHours { get; init; }

    /// <summary>Maximum estimated hours.</summary>
    public required double MaxHours { get; init; }
}
