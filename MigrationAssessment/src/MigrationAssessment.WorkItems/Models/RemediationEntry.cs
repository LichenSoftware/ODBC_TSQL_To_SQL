namespace MigrationAssessment.WorkItems.Models;

/// <summary>
/// A remediation guidance entry from the knowledge base.
/// </summary>
public sealed record RemediationEntry
{
    /// <summary>The PostgreSQL equivalent pattern/syntax</summary>
    public required string PostgresEquivalent { get; init; }

    /// <summary>Step-by-step remediation instructions</summary>
    public required string RemediationSteps { get; init; }

    /// <summary>Why the SQL Server construct is incompatible</summary>
    public required string IncompatibilityExplanation { get; init; }

    /// <summary>Risk level this entry applies to</summary>
    public required int RiskLevel { get; init; }

    /// <summary>Whether this requires architectural review</summary>
    public bool RequiresArchitecturalReview { get; init; }

    /// <summary>Relevant PostgreSQL documentation area</summary>
    public string? PostgresDocReference { get; init; }
}
