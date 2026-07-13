namespace SchemaConversion.Core.Models;

public sealed record ClassificationResult
{
    public required ConversionMethod Method { get; init; }
    public required string Reason { get; init; }
}
