namespace SchemaConversion.Core.Options;

public sealed record SchemaExtractionOptions
{
    public string? ConnectionString { get; init; }
    public IReadOnlyList<string>? FilePaths { get; init; }
    public IReadOnlyList<string>? IncludeSchemas { get; init; }
}
