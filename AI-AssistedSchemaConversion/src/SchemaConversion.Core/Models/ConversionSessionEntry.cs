namespace SchemaConversion.Core.Models;

public sealed record ConversionSessionEntry
{
    public required SchemaObject Source { get; init; }
    public required ConversionResult Result { get; init; }
    public DateTimeOffset ConvertedAt { get; init; }
    public bool IsManuallyEdited { get; init; }
}
