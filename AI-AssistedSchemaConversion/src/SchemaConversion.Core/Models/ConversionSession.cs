namespace SchemaConversion.Core.Models;

public sealed record ConversionSession
{
    public required string SessionId { get; init; }
    public DateTimeOffset CreatedAt { get; init; }
    public DateTimeOffset LastModifiedAt { get; init; }
    public string? SourceType { get; init; }
    public string? SourceIdentifier { get; init; }
    public int TotalObjectCount { get; init; }
    public ConversionFilters? Filters { get; init; }
}

public sealed record ConversionFilters
{
    public IReadOnlyList<string>? Schemas { get; init; }
    public IReadOnlyList<SchemaObjectType>? Types { get; init; }
    public IReadOnlyList<string>? Objects { get; init; }
}
