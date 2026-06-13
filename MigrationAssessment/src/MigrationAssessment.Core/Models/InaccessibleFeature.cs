namespace MigrationAssessment.Core.Models;

/// <summary>
/// A feature category that could not be checked due to insufficient permissions.
/// </summary>
public sealed record InaccessibleFeature
{
    public required string FeatureCategory { get; init; }
    public required string RequiredPermission { get; init; }
}
