using MigrationAssessment.Core.Models;

namespace MigrationAssessment.Core.Interfaces;

/// <summary>
/// Generates the final assessment report from all collected and analyzed data.
/// </summary>
public interface IReportGenerator
{
    /// <summary>
    /// Generates a complete assessment report.
    /// </summary>
    /// <param name="statements">All analyzed statements with risk scores.</param>
    /// <param name="objectInventory">Database object metadata inventory.</param>
    /// <param name="featureDetection">Server feature detection results.</param>
    /// <param name="failures">Collection failures from any sources that failed.</param>
    /// <returns>The complete assessment report.</returns>
    AssessmentReport GenerateReport(
        IReadOnlyList<AnalyzedStatement> statements,
        DatabaseObjectInventory objectInventory,
        FeatureDetectionResult featureDetection,
        IReadOnlyList<CollectionFailure> failures);
}
