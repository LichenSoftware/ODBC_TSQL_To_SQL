using MigrationAssessment.Core.Models;

namespace MigrationAssessment.Core.Interfaces;

/// <summary>
/// Analyzes schema DDL metadata to detect SQL Server-specific patterns
/// that require conversion for PostgreSQL migration.
/// </summary>
public interface ISchemaAnalyzer
{
    /// <summary>
    /// Analyzes the database object inventory for schema-level migration issues
    /// including data type mappings, identity columns, clustered indexes,
    /// collation differences, and computed columns.
    /// </summary>
    /// <param name="objectInventory">The database object inventory from metadata collection.</param>
    /// <returns>Schema analysis result with findings and effort estimate.</returns>
    SchemaAnalysisResult Analyze(DatabaseObjectInventory objectInventory);
}
