namespace MigrationAssessment.Core.Models;

/// <summary>
/// Represents a named database object identified from parsed SQL source,
/// along with aggregated risk and feature information for migration assessment.
/// </summary>
public sealed record ObjectInventoryEntry
{
    /// <summary>The object name (e.g., "usp_CreateOrder").</summary>
    public required string Name { get; init; }

    /// <summary>One of: StoredProcedure, View, ScalarFunction, TableValuedFunction, Trigger, AdHoc.</summary>
    public required string Type { get; init; }

    /// <summary>Number of SQL statements inside the object.</summary>
    public required int StatementCount { get; init; }

    /// <summary>Highest risk score of any statement within the object.</summary>
    public required int MaxRiskScore { get; init; }

    /// <summary>Distinct conversion categories detected (e.g., "automatic", "manual").</summary>
    public required IReadOnlyList<string> ConversionCategories { get; init; }

    /// <summary>Flat list of all distinct SQL Server features detected across all statements in this object.</summary>
    public required IReadOnlyList<string> DetectedFeatures { get; init; }
}
