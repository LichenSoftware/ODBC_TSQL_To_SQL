namespace SchemaConversion.Core.Models;

public sealed record ConversionResult
{
    public required string ObjectName { get; init; }
    public required string SchemaName { get; init; }
    public required SchemaObjectType ObjectType { get; init; }
    public required ConversionStatus Status { get; init; }
    public required ConversionMethod Method { get; init; }
    public string? GeneratedDdl { get; init; }
    public string? WrapperDdl { get; init; }
    public double? ConfidenceScore { get; init; }
    public IReadOnlyList<string> Assumptions { get; init; } = [];
    public IReadOnlyList<ManualReviewFlag> ReviewFlags { get; init; } = [];
    public IReadOnlyList<CompatibilityNote> CompatibilityNotes { get; init; } = [];
    public string? PromptTemplateVersion { get; init; }
    public string? ErrorMessage { get; init; }
}
