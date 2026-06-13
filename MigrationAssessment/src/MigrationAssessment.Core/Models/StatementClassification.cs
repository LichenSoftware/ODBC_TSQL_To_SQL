namespace MigrationAssessment.Core.Models;

/// <summary>
/// Classifies a T-SQL statement by its primary operation type.
/// </summary>
public enum StatementClassification
{
    Select,
    Insert,
    Update,
    Delete,
    Merge,
    Ddl,
    Dcl,
    Tcl,
    Procedural,
    Unknown
}
