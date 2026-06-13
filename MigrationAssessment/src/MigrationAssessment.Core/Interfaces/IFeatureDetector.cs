using System.Data.Common;
using MigrationAssessment.Core.Models;

namespace MigrationAssessment.Core.Interfaces;

/// <summary>
/// Detects SQL Server-specific features that may impact migration to PostgreSQL.
/// Queries system views across 13 feature categories and reports inventory, counts, and permissions issues.
/// </summary>
public interface IFeatureDetector
{
    /// <summary>
    /// Detects server-level features across all categories.
    /// </summary>
    /// <param name="connection">An open database connection.</param>
    /// <param name="options">Collection options including timeouts.</param>
    /// <param name="ct">Cancellation token for cooperative cancellation.</param>
    /// <returns>Feature detection result with counts, inventory, and inaccessible features.</returns>
    Task<FeatureDetectionResult> DetectAsync(DbConnection connection, CollectionOptions options, CancellationToken ct);
}
