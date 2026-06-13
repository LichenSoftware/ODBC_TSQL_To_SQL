namespace MigrationAssessment.Core.Models;

/// <summary>
/// Categorizes a detected SQL Server-specific feature within a statement.
/// </summary>
public enum FeatureCategory
{
    QueryFeature,
    FunctionUsage,
    TemporaryObject,
    TransactionFeature
}
