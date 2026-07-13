using SchemaConversion.Core.Models;

namespace SchemaConversion.Core.Options;

public sealed record ConversionPipelineOptions
{
    public required string SessionId { get; init; }
    public required SchemaExtractionOptions Extraction { get; init; }
    public ConversionFilters? Filters { get; init; }
    public int Concurrency { get; init; } = 4;
    public IReadOnlyList<string>? ForceAiObjects { get; init; }
    public IReadOnlyList<string>? ForceRulesObjects { get; init; }
}
