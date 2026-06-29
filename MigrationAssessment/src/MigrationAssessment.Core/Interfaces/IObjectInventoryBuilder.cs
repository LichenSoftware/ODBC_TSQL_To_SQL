using MigrationAssessment.Core.Interfaces;
using MigrationAssessment.Core.Models;

namespace MigrationAssessment.Core.Interfaces;

/// <summary>
/// Builds the object inventory by correlating analyzed statements with known database objects
/// (from metadata collection) and parsing their source definitions to produce per-object
/// risk and feature summaries.
/// </summary>
public interface IObjectInventoryBuilder
{
    /// <summary>
    /// Builds the enriched object inventory by correlating analyzed statements with the
    /// programmable objects discovered by the metadata collector. Each object gets per-object
    /// stats: statement count, max risk, conversion categories, and detected features.
    /// Statements not attributable to any named object are grouped under "Ad Hoc".
    /// </summary>
    /// <param name="statements">The fully analyzed statements (e.g. from Query Store).</param>
    /// <param name="objectInventory">The database object inventory from the metadata collector.</param>
    /// <returns>A list of object inventory entries with per-object risk and feature details.</returns>
    IReadOnlyList<ObjectInventoryEntry> BuildInventory(
        IReadOnlyList<AnalyzedStatement> statements,
        DatabaseObjectInventory objectInventory);
}
