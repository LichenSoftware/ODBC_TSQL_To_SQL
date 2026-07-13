namespace SchemaConversion.Core.Models;

public sealed record CompatibilityNote
{
    public required string Category { get; init; }
    public required string Description { get; init; }
}
