using MigrationAssessment.Core.Models;

namespace MigrationAssessment.WorkItems.Models;

/// <summary>
/// Result of reading an assessment JSON file.
/// </summary>
public sealed record AssessmentReadResult
{
    /// <summary>Whether the read operation succeeded.</summary>
    public required bool Succeeded { get; init; }

    /// <summary>Error or informational message (null when succeeded with data).</summary>
    public string? ErrorMessage { get; init; }

    /// <summary>Parsed analyzed statements (null when failed).</summary>
    public IReadOnlyList<AnalyzedStatement>? Statements { get; init; }

    /// <summary>Parsed feature detection result (null when failed).</summary>
    public FeatureDetectionResult? FeatureDetection { get; init; }

    /// <summary>Parsed object inventory entries (null when not present or failed).</summary>
    public IReadOnlyList<ObjectInventoryEntry>? ObjectInventory { get; init; }
}
