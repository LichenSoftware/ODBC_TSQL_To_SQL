using System.Data.Common;
using MigrationAssessment.Core.Models;

namespace MigrationAssessment.Core.Interfaces;

/// <summary>
/// Collects database object metadata (tables, indexes, constraints, etc.) from SQL Server catalog views.
/// </summary>
public interface IMetadataCollector
{
    /// <summary>
    /// Collects a complete inventory of database objects from the connected SQL Server instance.
    /// </summary>
    /// <param name="connection">An open database connection.</param>
    /// <param name="options">Collection options including timeouts.</param>
    /// <param name="ct">Cancellation token for cooperative cancellation.</param>
    /// <returns>A complete inventory of database objects organized by schema and type.</returns>
    Task<DatabaseObjectInventory> CollectAsync(DbConnection connection, CollectionOptions options, CancellationToken ct);
}
