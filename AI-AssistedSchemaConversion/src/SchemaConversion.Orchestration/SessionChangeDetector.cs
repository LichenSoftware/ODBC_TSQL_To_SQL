using Microsoft.Extensions.Logging;
using SchemaConversion.Core.Models;

namespace SchemaConversion.Orchestration;

/// <summary>
/// Compares source definition SHA-256 hashes between current extraction and stored session
/// to identify new or modified objects that need (re)processing.
/// </summary>
public sealed class SessionChangeDetector
{
    private readonly ILogger<SessionChangeDetector> _logger;

    public SessionChangeDetector(ILogger<SessionChangeDetector> logger)
    {
        _logger = logger;
    }

    /// <summary>
    /// Identifies objects that require processing: new objects or objects whose source hash has changed.
    /// Objects marked as ManuallyReviewed are excluded from automatic reprocessing.
    /// </summary>
    /// <param name="currentObjects">Currently extracted schema objects from source.</param>
    /// <param name="existingEntries">Previously stored session entries.</param>
    /// <param name="filters">Optional filter criteria to apply.</param>
    /// <returns>List of objects that need to be processed.</returns>
    public IReadOnlyList<SchemaObject> GetObjectsRequiringProcessing(
        IReadOnlyList<SchemaObject> currentObjects,
        IReadOnlyList<ConversionSessionEntry> existingEntries,
        ConversionFilters? filters)
    {
        ArgumentNullException.ThrowIfNull(currentObjects);
        ArgumentNullException.ThrowIfNull(existingEntries);

        // Build lookup of existing entries by schema+name
        var existingLookup = existingEntries.ToDictionary(
            e => GetObjectKey(e.Source.SchemaName, e.Source.Name),
            e => e,
            StringComparer.OrdinalIgnoreCase);

        var result = new List<SchemaObject>();

        foreach (var obj in currentObjects)
        {
            // Apply filters first
            if (!PassesFilter(obj, filters))
                continue;

            var key = GetObjectKey(obj.SchemaName, obj.Name);

            if (!existingLookup.TryGetValue(key, out var existingEntry))
            {
                // New object - not in session
                _logger.LogDebug("Object {Schema}.{Name} is new (not in session)",
                    obj.SchemaName, obj.Name);
                result.Add(obj);
            }
            else if (existingEntry.Result.Status == ConversionStatus.ManuallyReviewed)
            {
                // Skip objects marked as manually reviewed
                _logger.LogDebug("Object {Schema}.{Name} skipped - manually reviewed",
                    obj.SchemaName, obj.Name);
            }
            else if (existingEntry.Result.Status == ConversionStatus.Pending
                     || existingEntry.Result.Status == ConversionStatus.Failed)
            {
                // Pending or Failed objects always need (re)processing
                _logger.LogDebug("Object {Schema}.{Name} is {Status} - will process",
                    obj.SchemaName, obj.Name, existingEntry.Result.Status);
                result.Add(obj);
            }
            else if (!string.Equals(obj.SourceDefinitionHash, existingEntry.Source.SourceDefinitionHash, StringComparison.OrdinalIgnoreCase))
            {
                // Modified object - hash differs
                _logger.LogDebug("Object {Schema}.{Name} is modified (hash changed: {OldHash} → {NewHash})",
                    obj.SchemaName, obj.Name,
                    existingEntry.Source.SourceDefinitionHash[..Math.Min(8, existingEntry.Source.SourceDefinitionHash.Length)],
                    obj.SourceDefinitionHash[..Math.Min(8, obj.SourceDefinitionHash.Length)]);
                result.Add(obj);
            }
        }

        _logger.LogInformation(
            "Change detection complete: {Total} source objects, {NeedProcessing} require processing",
            currentObjects.Count, result.Count);

        return result;
    }

    private static bool PassesFilter(SchemaObject obj, ConversionFilters? filters)
    {
        if (filters is null)
            return true;

        // Schema filter
        if (filters.Schemas is { Count: > 0 } schemas)
        {
            if (!schemas.Any(s => string.Equals(s, obj.SchemaName, StringComparison.OrdinalIgnoreCase)))
                return false;
        }

        // Object type filter
        if (filters.Types is { Count: > 0 } types)
        {
            if (!types.Contains(obj.ObjectType))
                return false;
        }

        // Explicit object list filter
        if (filters.Objects is { Count: > 0 } objects)
        {
            var qualifiedName = $"{obj.SchemaName}.{obj.Name}";
            if (!objects.Any(o => string.Equals(o, qualifiedName, StringComparison.OrdinalIgnoreCase) ||
                                  string.Equals(o, obj.Name, StringComparison.OrdinalIgnoreCase)))
                return false;
        }

        return true;
    }

    private static string GetObjectKey(string schemaName, string objectName)
    {
        return $"{schemaName}.{objectName}";
    }
}
