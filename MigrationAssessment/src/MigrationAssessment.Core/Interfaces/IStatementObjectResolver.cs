using MigrationAssessment.Core.Models;

namespace MigrationAssessment.Core.Interfaces;

/// <summary>
/// Resolves which named database object (stored procedure, view, function, trigger)
/// a given analyzed statement belongs to. Used by both the ObjectInventoryBuilder and
/// the work item generator to ensure consistent object attribution.
/// </summary>
public interface IStatementObjectResolver
{
    /// <summary>
    /// Builds a lookup that maps each analyzed statement to its containing named object,
    /// based on normalized text containment within the programmable object source definitions.
    /// Statements that cannot be matched to any named object are not included in the result.
    /// </summary>
    /// <param name="statements">The analyzed statements (e.g., from Query Store).</param>
    /// <param name="objectInventory">The database object inventory from the metadata collector.</param>
    /// <returns>
    /// A dictionary mapping each statement (by reference) to a tuple of (ObjectName, ObjectType).
    /// Only statements that can be attributed to a named object are included.
    /// </returns>
    IReadOnlyDictionary<AnalyzedStatement, ResolvedObject> ResolveStatementObjects(
        IReadOnlyList<AnalyzedStatement> statements,
        DatabaseObjectInventory objectInventory);
}

/// <summary>
/// The resolved object attribution for a statement.
/// </summary>
public sealed record ResolvedObject
{
    /// <summary>The object name (e.g., "sp_UpdateStockWithLock").</summary>
    public required string Name { get; init; }

    /// <summary>One of: StoredProcedure, View, ScalarFunction, TableValuedFunction, Trigger.</summary>
    public required string Type { get; init; }
}
