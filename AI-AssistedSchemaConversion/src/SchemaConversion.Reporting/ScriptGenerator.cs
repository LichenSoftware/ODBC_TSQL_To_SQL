using System.Text;
using Microsoft.Extensions.Logging;
using SchemaConversion.Core.Interfaces;
using SchemaConversion.Core.Models;
using SchemaConversion.Core.Options;

namespace SchemaConversion.Reporting;

/// <summary>
/// Produces dependency-ordered PostgreSQL DDL scripts.
/// Supports multiple output modes: consolidated, per-schema, per-type, per-object.
/// Uses IF NOT EXISTS / CREATE OR REPLACE patterns and includes comments.
/// </summary>
public sealed class ScriptGenerator : IScriptGenerator
{
    private readonly ScriptOrderResolver _orderResolver;
    private readonly ILogger<ScriptGenerator> _logger;

    public ScriptGenerator(ScriptOrderResolver orderResolver, ILogger<ScriptGenerator> logger)
    {
        _orderResolver = orderResolver;
        _logger = logger;
    }

    public async Task GenerateAsync(
        IReadOnlyList<ConversionSessionEntry> entries,
        ScriptGenerationOptions options,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(entries);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentException.ThrowIfNullOrWhiteSpace(options.OutputDirectory);

        ct.ThrowIfCancellationRequested();

        _logger.LogInformation("Generating scripts in {Mode} mode to {OutputDir}",
            options.Mode, options.OutputDirectory);

        var orderedEntries = _orderResolver.Resolve(entries);

        _logger.LogDebug("Resolved {Count} ordered script entries", orderedEntries.Count);

        Directory.CreateDirectory(options.OutputDirectory);

        switch (options.Mode)
        {
            case ScriptOutputMode.Consolidated:
                await WriteConsolidatedAsync(orderedEntries, options, ct).ConfigureAwait(false);
                break;
            case ScriptOutputMode.PerSchema:
                await WritePerSchemaAsync(orderedEntries, options, ct).ConfigureAwait(false);
                break;
            case ScriptOutputMode.PerType:
                await WritePerTypeAsync(orderedEntries, options, ct).ConfigureAwait(false);
                break;
            case ScriptOutputMode.PerObject:
                await WritePerObjectAsync(orderedEntries, options, ct).ConfigureAwait(false);
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(options), $"Unknown output mode: {options.Mode}");
        }

        _logger.LogInformation("Script generation complete for {Count} entries", orderedEntries.Count);
    }

    private async Task WriteConsolidatedAsync(
        IReadOnlyList<OrderedScriptEntry> entries,
        ScriptGenerationOptions options,
        CancellationToken ct)
    {
        var filePath = Path.Combine(options.OutputDirectory, "migration.sql");
        var content = BuildScript(entries, options.IncludeComments);

        await File.WriteAllTextAsync(filePath, content, Encoding.UTF8, ct).ConfigureAwait(false);

        _logger.LogDebug("Wrote consolidated script to {FilePath}", filePath);
    }

    private async Task WritePerSchemaAsync(
        IReadOnlyList<OrderedScriptEntry> entries,
        ScriptGenerationOptions options,
        CancellationToken ct)
    {
        var schemaGroups = entries
            .GroupBy(e => e.Entry.Source.SchemaName, StringComparer.OrdinalIgnoreCase)
            .OrderBy(g => g.Key);

        foreach (var group in schemaGroups)
        {
            ct.ThrowIfCancellationRequested();

            var schemaDir = Path.Combine(options.OutputDirectory, SanitizeFileName(group.Key));
            Directory.CreateDirectory(schemaDir);

            var filePath = Path.Combine(schemaDir, $"{SanitizeFileName(group.Key)}.sql");
            var content = BuildScript(group.ToList(), options.IncludeComments);

            await File.WriteAllTextAsync(filePath, content, Encoding.UTF8, ct).ConfigureAwait(false);

            _logger.LogDebug("Wrote per-schema script for {Schema} to {FilePath}", group.Key, filePath);
        }
    }

    private async Task WritePerTypeAsync(
        IReadOnlyList<OrderedScriptEntry> entries,
        ScriptGenerationOptions options,
        CancellationToken ct)
    {
        var typeGroups = entries
            .GroupBy(e => e.IsWrapper ? "Wrappers" : e.Entry.Source.ObjectType.ToString())
            .OrderBy(g => entries.Where(e =>
                (e.IsWrapper ? "Wrappers" : e.Entry.Source.ObjectType.ToString()) == g.Key)
                .Min(e => e.CategoryOrder));

        foreach (var group in typeGroups)
        {
            ct.ThrowIfCancellationRequested();

            var fileName = $"{SanitizeFileName(group.Key).ToLowerInvariant()}.sql";
            var filePath = Path.Combine(options.OutputDirectory, fileName);
            var content = BuildScript(group.ToList(), options.IncludeComments);

            await File.WriteAllTextAsync(filePath, content, Encoding.UTF8, ct).ConfigureAwait(false);

            _logger.LogDebug("Wrote per-type script for {Type} to {FilePath}", group.Key, filePath);
        }
    }

    private async Task WritePerObjectAsync(
        IReadOnlyList<OrderedScriptEntry> entries,
        ScriptGenerationOptions options,
        CancellationToken ct)
    {
        var sequenceNumber = 1;

        foreach (var entry in entries)
        {
            ct.ThrowIfCancellationRequested();

            var schemaDir = Path.Combine(options.OutputDirectory, SanitizeFileName(entry.Entry.Source.SchemaName));
            Directory.CreateDirectory(schemaDir);

            var wrapperSuffix = entry.IsWrapper ? "_wrapper" : "";
            var fileName = $"{sequenceNumber:D4}_{SanitizeFileName(entry.Entry.Source.Name)}{wrapperSuffix}.sql";
            var filePath = Path.Combine(schemaDir, fileName);

            var content = BuildScript([entry], options.IncludeComments);

            await File.WriteAllTextAsync(filePath, content, Encoding.UTF8, ct).ConfigureAwait(false);

            sequenceNumber++;
        }

        _logger.LogDebug("Wrote {Count} per-object scripts", entries.Count);
    }

    private static string BuildScript(IReadOnlyList<OrderedScriptEntry> entries, bool includeComments)
    {
        var sb = new StringBuilder();

        if (includeComments)
        {
            sb.AppendLine("-- =============================================================================");
            sb.AppendLine($"-- Generated by Schema Conversion Tool at {DateTimeOffset.UtcNow:yyyy-MM-dd HH:mm:ss} UTC");
            sb.AppendLine("-- =============================================================================");
            sb.AppendLine();
        }

        var currentCategory = -1;

        foreach (var entry in entries)
        {
            if (includeComments && entry.CategoryOrder != currentCategory)
            {
                currentCategory = entry.CategoryOrder;
                var categoryName = GetCategoryDisplayName(entry);
                sb.AppendLine($"-- -----------------------------------------------------------------------------");
                sb.AppendLine($"-- {categoryName}");
                sb.AppendLine($"-- -----------------------------------------------------------------------------");
                sb.AppendLine();
            }

            if (includeComments)
            {
                var objectDesc = entry.IsWrapper
                    ? $"Wrapper for: {entry.Entry.Source.SchemaName}.{entry.Entry.Source.Name}"
                    : $"{entry.Entry.Source.ObjectType}: {entry.Entry.Source.SchemaName}.{entry.Entry.Source.Name}";
                sb.AppendLine($"-- {objectDesc}");
                sb.AppendLine($"-- Method: {entry.Entry.Result.Method}, Status: {entry.Entry.Result.Status}");

                if (entry.Entry.Result.ConfidenceScore.HasValue)
                {
                    sb.AppendLine($"-- Confidence: {entry.Entry.Result.ConfidenceScore.Value:F2}");
                }

                sb.AppendLine();
            }

            sb.AppendLine(entry.Ddl.TrimEnd());

            // Ensure statements end with a semicolon
            if (!entry.Ddl.TrimEnd().EndsWith(';'))
            {
                sb.AppendLine(";");
            }

            sb.AppendLine();
        }

        return sb.ToString();
    }

    private static string GetCategoryDisplayName(OrderedScriptEntry entry)
    {
        if (entry.IsWrapper)
            return "Wrapper Objects (compatibility layer)";

        return entry.CategoryOrder switch
        {
            0 => "Schema Definitions",
            1 => "User-Defined Types and Domains",
            2 => "Sequences",
            3 => "Tables and Constraints",
            4 => "Indexes",
            5 => "Functions and Procedures",
            6 => "Triggers",
            7 => "Views",
            9 => "Permissions (GRANT / REVOKE)",
            10 => "Comments",
            _ => "Other Objects"
        };
    }

    private static string SanitizeFileName(string name)
    {
        var invalidChars = Path.GetInvalidFileNameChars();
        var sanitized = new StringBuilder(name.Length);

        foreach (var c in name)
        {
            sanitized.Append(invalidChars.Contains(c) ? '_' : c);
        }

        return sanitized.ToString();
    }
}
