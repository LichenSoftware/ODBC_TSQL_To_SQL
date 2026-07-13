namespace SchemaConversion.Orchestration;

/// <summary>
/// Result of a full conversion pipeline execution.
/// </summary>
public sealed record ConversionPipelineResult
{
    public required int TotalProcessed { get; init; }
    public required int Converted { get; init; }
    public required int Flagged { get; init; }
    public required int Failed { get; init; }
    public required TimeSpan Duration { get; init; }
}
