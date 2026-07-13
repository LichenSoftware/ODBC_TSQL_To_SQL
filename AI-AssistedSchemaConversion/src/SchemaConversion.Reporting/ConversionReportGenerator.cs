using Microsoft.Extensions.Logging;
using SchemaConversion.Core.Interfaces;
using SchemaConversion.Core.Models;

namespace SchemaConversion.Reporting;

/// <summary>
/// Aggregates conversion session entries into a structured JSON report
/// with per-object details, summary statistics, and compatibility notes.
/// </summary>
public sealed class ConversionReportGenerator : IConversionReportGenerator
{
    private readonly ILogger<ConversionReportGenerator> _logger;

    public ConversionReportGenerator(ILogger<ConversionReportGenerator> logger)
    {
        _logger = logger;
    }

    public Task<ConversionReport> GenerateAsync(
        string sessionId, IReadOnlyList<ConversionSessionEntry> entries, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        ArgumentNullException.ThrowIfNull(entries);

        ct.ThrowIfCancellationRequested();

        _logger.LogInformation("Generating conversion report for session {SessionId} with {EntryCount} entries",
            sessionId, entries.Count);

        var summary = BuildSummary(entries);
        var compatibilityNotes = AggregateCompatibilityNotes(entries);
        var flaggedObjects = GetFlaggedObjects(entries);

        var report = new ConversionReport
        {
            SessionId = sessionId,
            GeneratedAt = DateTimeOffset.UtcNow,
            Summary = summary,
            Objects = entries,
            CompatibilityNotes = compatibilityNotes,
            FlaggedObjects = flaggedObjects
        };

        _logger.LogInformation(
            "Report generated: {TotalObjects} objects, {ProgressPercent:F1}% complete, {FlaggedCount} flagged",
            summary.TotalObjects, summary.ProgressPercent, flaggedObjects.Count);

        return Task.FromResult(report);
    }

    private static ConversionReportSummary BuildSummary(IReadOnlyList<ConversionSessionEntry> entries)
    {
        var totalObjects = entries.Count;

        var byStatus = entries
            .GroupBy(e => e.Result.Status)
            .ToDictionary(g => g.Key, g => g.Count())
            as IReadOnlyDictionary<ConversionStatus, int>;

        var byMethod = entries
            .GroupBy(e => e.Result.Method)
            .ToDictionary(g => g.Key, g => g.Count())
            as IReadOnlyDictionary<ConversionMethod, int>;

        var byType = entries
            .GroupBy(e => e.Source.ObjectType)
            .ToDictionary(g => g.Key, g => g.Count())
            as IReadOnlyDictionary<SchemaObjectType, int>;

        var progressPercent = CalculateProgressPercent(entries);

        return new ConversionReportSummary
        {
            TotalObjects = totalObjects,
            ByStatus = byStatus,
            ByMethod = byMethod,
            ByType = byType,
            ProgressPercent = progressPercent
        };
    }

    private static double CalculateProgressPercent(IReadOnlyList<ConversionSessionEntry> entries)
    {
        if (entries.Count == 0)
            return 0.0;

        var completedCount = entries.Count(e =>
            e.Result.Status is ConversionStatus.Converted
                or ConversionStatus.Flagged
                or ConversionStatus.ManuallyReviewed
                or ConversionStatus.OutOfScope);

        return Math.Round((double)completedCount / entries.Count * 100.0, 1);
    }

    private static IReadOnlyList<CompatibilityNote> AggregateCompatibilityNotes(
        IReadOnlyList<ConversionSessionEntry> entries)
    {
        return entries
            .SelectMany(e => e.Result.CompatibilityNotes)
            .GroupBy(n => new { n.Category, n.Description })
            .Select(g => g.First())
            .OrderBy(n => n.Category)
            .ThenBy(n => n.Description)
            .ToList();
    }

    private static IReadOnlyList<ConversionSessionEntry> GetFlaggedObjects(
        IReadOnlyList<ConversionSessionEntry> entries)
    {
        return entries
            .Where(e => e.Result.Status == ConversionStatus.Flagged
                        || e.Result.ReviewFlags.Count > 0)
            .ToList();
    }
}
