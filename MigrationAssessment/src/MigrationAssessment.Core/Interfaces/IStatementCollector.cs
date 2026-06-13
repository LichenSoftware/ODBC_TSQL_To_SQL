using System.Data.Common;
using MigrationAssessment.Core.Models;

namespace MigrationAssessment.Core.Interfaces;

/// <summary>
/// Collects SQL statements from a specific data source (Query Store, Extended Events, etc.).
/// </summary>
public interface IStatementCollector
{
    /// <summary>
    /// Gets the human-readable name identifying this collector source.
    /// </summary>
    string SourceName { get; }

    /// <summary>
    /// Collects SQL statements from the connected database.
    /// </summary>
    /// <param name="connection">An open database connection.</param>
    /// <param name="options">Collection options including timeouts and batch sizes.</param>
    /// <param name="ct">Cancellation token for cooperative cancellation.</param>
    /// <returns>The collection result containing statements and status.</returns>
    Task<CollectionResult> CollectAsync(DbConnection connection, CollectionOptions options, CancellationToken ct);
}
