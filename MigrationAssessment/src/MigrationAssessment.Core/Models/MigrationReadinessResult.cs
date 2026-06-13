namespace MigrationAssessment.Core.Models;

/// <summary>
/// Result of the migration readiness score calculation.
/// </summary>
public sealed record MigrationReadinessResult
{
    /// <summary>
    /// The readiness score (0-100), or null when insufficient data is available.
    /// </summary>
    public int? Score { get; init; }

    public required string Classification { get; init; }
    public required bool HasSufficientData { get; init; }
}
