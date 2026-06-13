namespace MigrationAssessment.Core.Models;

/// <summary>
/// Result of server-level feature detection across all feature categories.
/// </summary>
public sealed record FeatureDetectionResult
{
    public required IReadOnlyDictionary<string, int> FeatureCounts { get; init; }
    public required IReadOnlyList<DetectedServerFeature> DetailedInventory { get; init; }
    public required IReadOnlyList<InaccessibleFeature> InaccessibleFeatures { get; init; }
}
