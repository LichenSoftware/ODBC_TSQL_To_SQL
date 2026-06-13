namespace MigrationAssessment.Core.Models;

/// <summary>
/// A detected server-level feature with its category, object name, and properties.
/// </summary>
public sealed record DetectedServerFeature
{
    public required string FeatureCategory { get; init; }
    public required string ObjectName { get; init; }
    public IReadOnlyDictionary<string, string> Properties { get; init; } = new Dictionary<string, string>();
}
