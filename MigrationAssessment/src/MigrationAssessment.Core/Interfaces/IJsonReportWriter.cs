using MigrationAssessment.Core.Models;

namespace MigrationAssessment.Core.Interfaces;

/// <summary>
/// Writes an assessment report to a JSON file matching the published schema.
/// </summary>
public interface IJsonReportWriter
{
    /// <summary>
    /// Serializes the assessment report and supporting data to a JSON file.
    /// </summary>
    /// <param name="report">The generated assessment report.</param>
    /// <param name="statements">All analyzed statements with risk scores.</param>
    /// <param name="objectInventory">Database object metadata inventory.</param>
    /// <param name="featureDetection">Server feature detection results.</param>
    /// <param name="outputPath">The file path to write the JSON output to.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>A result indicating success or failure with error details.</returns>
    Task<JsonWriteResult> WriteAsync(
        AssessmentReport report,
        IReadOnlyList<AnalyzedStatement> statements,
        DatabaseObjectInventory objectInventory,
        FeatureDetectionResult featureDetection,
        string outputPath,
        CancellationToken ct);
}

/// <summary>
/// Result of a JSON report write operation.
/// </summary>
public sealed record JsonWriteResult
{
    /// <summary>
    /// Whether the write operation succeeded.
    /// </summary>
    public bool Succeeded { get; init; }

    /// <summary>
    /// Error message if the write operation failed.
    /// </summary>
    public string? ErrorMessage { get; init; }

    /// <summary>
    /// The output file path where the JSON was written (on success).
    /// </summary>
    public string? OutputPath { get; init; }
}
