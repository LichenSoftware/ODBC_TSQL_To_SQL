namespace SchemaConversion.Core.Models;

public sealed record ManualReviewFlag
{
    public required string Reason { get; init; }
    public string? CodeSection { get; init; }
    public string? SuggestedAlternative { get; init; }
}
