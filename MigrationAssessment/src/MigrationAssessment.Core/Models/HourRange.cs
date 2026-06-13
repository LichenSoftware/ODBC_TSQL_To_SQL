namespace MigrationAssessment.Core.Models;

/// <summary>
/// A range of estimated hours (minimum to maximum).
/// </summary>
public sealed record HourRange
{
    public required int MinHours { get; init; }
    public required int MaxHours { get; init; }
}
