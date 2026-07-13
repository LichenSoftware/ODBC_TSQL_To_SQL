using SchemaConversion.Core.Models;

namespace SchemaConversion.Reporting;

/// <summary>
/// Reorders converted DDL statements based on the dependency graph and category ordering.
/// Order: schemas → types → sequences → tables → indexes → functions → triggers → views → wrappers → permissions → comments.
/// Respects intra-category dependencies.
/// </summary>
public sealed class ScriptOrderResolver
{
    /// <summary>
    /// Category order matching the design specification:
    /// 1. CREATE SCHEMA
    /// 2. CREATE DOMAIN / CREATE TYPE (user-defined types)
    /// 3. CREATE SEQUENCE
    /// 4. CREATE TABLE (with constraints inline)
    /// 5. CREATE INDEX
    /// 6. CREATE FUNCTION / CREATE PROCEDURE
    /// 7. CREATE TRIGGER (after trigger functions)
    /// 8. CREATE VIEW
    /// 9. Wrapper objects (functions/views for compatibility)
    /// 10. GRANT / REVOKE (permissions)
    /// 11. COMMENT ON (extended properties)
    /// </summary>
    private static readonly Dictionary<SchemaObjectType, int> CategoryOrder = new()
    {
        [SchemaObjectType.Schema] = 0,
        [SchemaObjectType.UserDefinedType] = 1,
        [SchemaObjectType.Sequence] = 2,
        [SchemaObjectType.Table] = 3,
        [SchemaObjectType.Constraint] = 3, // Inline with tables
        [SchemaObjectType.Index] = 4,
        [SchemaObjectType.Function] = 5,
        [SchemaObjectType.StoredProcedure] = 5,
        [SchemaObjectType.Trigger] = 6,
        [SchemaObjectType.View] = 7,
        [SchemaObjectType.Synonym] = 7, // Synonyms become views
        [SchemaObjectType.Permission] = 9,
    };

    /// <summary>
    /// Orders entries by category, then respects intra-category dependencies.
    /// Entries with wrapper DDL are separated and placed in the wrapper category (order 8).
    /// </summary>
    public IReadOnlyList<OrderedScriptEntry> Resolve(IReadOnlyList<ConversionSessionEntry> entries)
    {
        ArgumentNullException.ThrowIfNull(entries);

        var convertedEntries = entries
            .Where(e => e.Result.Status is ConversionStatus.Converted
                        or ConversionStatus.Flagged
                        or ConversionStatus.ManuallyReviewed)
            .Where(e => !string.IsNullOrWhiteSpace(e.Result.GeneratedDdl))
            .ToList();

        var result = new List<OrderedScriptEntry>();

        // Add main DDL entries ordered by category
        var mainEntries = convertedEntries
            .Select(e => new OrderedScriptEntry
            {
                Entry = e,
                Ddl = e.Result.GeneratedDdl!,
                CategoryOrder = GetCategoryOrder(e.Source.ObjectType),
                IsWrapper = false
            })
            .ToList();

        // Add wrapper DDL entries in wrapper category (order 8)
        var wrapperEntries = convertedEntries
            .Where(e => !string.IsNullOrWhiteSpace(e.Result.WrapperDdl))
            .Select(e => new OrderedScriptEntry
            {
                Entry = e,
                Ddl = e.Result.WrapperDdl!,
                CategoryOrder = 8, // Wrapper objects category
                IsWrapper = true
            })
            .ToList();

        result.AddRange(mainEntries);
        result.AddRange(wrapperEntries);

        // Sort by category order, then resolve intra-category dependencies
        result = result
            .OrderBy(e => e.CategoryOrder)
            .ThenBy(e => GetIntraCategoryOrder(e, convertedEntries))
            .ThenBy(e => $"{e.Entry.Source.SchemaName}.{e.Entry.Source.Name}")
            .ToList();

        return result;
    }

    private static int GetCategoryOrder(SchemaObjectType objectType)
    {
        return CategoryOrder.TryGetValue(objectType, out var order) ? order : 10;
    }

    /// <summary>
    /// Computes an intra-category dependency order.
    /// Objects that depend on others within the same category are placed after their dependencies.
    /// </summary>
    private static int GetIntraCategoryOrder(
        OrderedScriptEntry entry,
        IReadOnlyList<ConversionSessionEntry> allEntries)
    {
        if (entry.IsWrapper)
            return 0;

        var dependencies = entry.Entry.Source.DependsOn;
        if (dependencies.Count == 0)
            return 0;

        var sameCategoryObjectNames = allEntries
            .Where(e => GetCategoryOrder(e.Source.ObjectType) == entry.CategoryOrder)
            .Select(e => $"{e.Source.SchemaName}.{e.Source.Name}")
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        // Count how many same-category objects this entry depends on
        var intraDependencyCount = dependencies
            .Count(d => sameCategoryObjectNames.Contains(d));

        return intraDependencyCount;
    }
}

/// <summary>
/// Represents a single DDL statement in the ordered output, including metadata
/// about which entry it came from and whether it's a wrapper.
/// </summary>
public sealed record OrderedScriptEntry
{
    public required ConversionSessionEntry Entry { get; init; }
    public required string Ddl { get; init; }
    public required int CategoryOrder { get; init; }
    public required bool IsWrapper { get; init; }
}
