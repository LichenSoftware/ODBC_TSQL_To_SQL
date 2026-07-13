namespace SchemaConversion.Core.Models;

public sealed record ConversionReport
{
    public required string SessionId { get; init; }
    public DateTimeOffset GeneratedAt { get; init; }
    public required ConversionReportSummary Summary { get; init; }
    public IReadOnlyList<ConversionSessionEntry> Objects { get; init; } = [];
    public IReadOnlyList<CompatibilityNote> CompatibilityNotes { get; init; } = [];
    public IReadOnlyList<ConversionSessionEntry> FlaggedObjects { get; init; } = [];
}

public sealed record ConversionReportSummary
{
    public int TotalObjects { get; init; }
    public IReadOnlyDictionary<ConversionStatus, int> ByStatus { get; init; } = new Dictionary<ConversionStatus, int>();
    public IReadOnlyDictionary<ConversionMethod, int> ByMethod { get; init; } = new Dictionary<ConversionMethod, int>();
    public IReadOnlyDictionary<SchemaObjectType, int> ByType { get; init; } = new Dictionary<SchemaObjectType, int>();
    public double ProgressPercent { get; init; }
}
