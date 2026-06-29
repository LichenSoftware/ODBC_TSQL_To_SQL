namespace MigrationAssessment.WorkItems.Models;

/// <summary>
/// Indicates the confidence in an effort estimate based on risk level
/// and the nature of the required conversion.
/// </summary>
public enum ConfidenceLevel
{
    /// <summary>
    /// Risk 1–2 items with known automatic substitutions (e.g., ISNULL → COALESCE).
    /// Range ratio should be ≤2x.
    /// </summary>
    High,

    /// <summary>
    /// Risk 3 items with semi-automatic conversion patterns.
    /// Range ratio should be 2–4x.
    /// </summary>
    Medium,

    /// <summary>
    /// Risk 4–5 items requiring design decisions, architectural changes,
    /// or XML/CLR involvement. Range remains wide (4–7x).
    /// </summary>
    Low
}
