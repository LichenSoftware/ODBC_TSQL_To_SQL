namespace MigrationAssessment.Core.Models;

/// <summary>
/// Identifies the origin of a collected SQL statement.
/// </summary>
public enum StatementSource
{
    QueryStore,
    ExtendedEvents,
    Metadata
}
