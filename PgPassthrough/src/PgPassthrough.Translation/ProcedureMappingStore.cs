using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;

namespace PgPassthrough.Translation;

/// <summary>
/// Loads and provides access to custom procedure-to-function mappings generated
/// from the schema conversion session. These mappings tell PgPassthrough how to
/// translate EXEC proc_name calls to SELECT * FROM function_name() calls.
/// </summary>
public sealed class ProcedureMappingStore
{
    private readonly Dictionary<string, ProcedureMapping> _mappings;
    private readonly ILogger<ProcedureMappingStore> _logger;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true
    };

    public ProcedureMappingStore(string? mappingFilePath, ILogger<ProcedureMappingStore> logger)
    {
        _logger = logger;
        _mappings = LoadMappings(mappingFilePath);
    }

    /// <summary>
    /// Looks up a procedure/function mapping by its original SQL Server name.
    /// Tries both schema-qualified and unqualified lookups.
    /// </summary>
    public ProcedureMapping? Lookup(string? schema, string name)
    {
        // Try fully qualified name first
        if (schema != null)
        {
            var key = $"{schema}.{name}";
            if (_mappings.TryGetValue(key, out var mapping))
                return mapping;
        }

        // Try just the name (for unqualified EXEC calls)
        if (_mappings.TryGetValue(name, out var byName))
            return byName;

        return null;
    }

    /// <summary>
    /// Returns all loaded mappings.
    /// </summary>
    public IReadOnlyDictionary<string, ProcedureMapping> All => _mappings;

    public int Count => _mappings.Count;

    private Dictionary<string, ProcedureMapping> LoadMappings(string? filePath)
    {
        var result = new Dictionary<string, ProcedureMapping>(StringComparer.OrdinalIgnoreCase);

        if (string.IsNullOrEmpty(filePath) || !File.Exists(filePath))
        {
            _logger.LogInformation("No procedure mapping file found at '{Path}'. Custom mappings disabled.", filePath ?? "(none)");
            return result;
        }

        try
        {
            var json = File.ReadAllText(filePath);
            var manifest = JsonSerializer.Deserialize<MappingManifest>(json, JsonOptions);

            if (manifest?.Mappings == null || manifest.Mappings.Count == 0)
            {
                _logger.LogInformation("Procedure mapping file loaded but contains no mappings.");
                return result;
            }

            foreach (var mapping in manifest.Mappings)
            {
                // Index by qualified name
                var qualifiedKey = $"{mapping.SourceSchema}.{mapping.SourceName}";
                result[qualifiedKey] = mapping;

                // Also index by unqualified name for EXEC calls without schema
                result[mapping.SourceName] = mapping;
            }

            _logger.LogInformation("Loaded {Count} procedure mappings from {Path}", manifest.Mappings.Count, filePath);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to load procedure mappings from {Path}", filePath);
        }

        return result;
    }

    // ─── Models ─────────────────────────────────────────────────────────────────

    private sealed class MappingManifest
    {
        public List<ProcedureMapping>? Mappings { get; set; }
    }
}

/// <summary>
/// Represents a mapping from a SQL Server procedure/function to its PostgreSQL equivalent.
/// </summary>
public sealed class ProcedureMapping
{
    public string SourceSchema { get; set; } = "";
    public string SourceName { get; set; } = "";
    public string SourceType { get; set; } = "";
    public string PostgresSchema { get; set; } = "";
    public string PostgresName { get; set; } = "";
    public string PostgresType { get; set; } = "";

    /// <summary>"SELECT" for functions (SELECT * FROM func()), "CALL" for procedures.</summary>
    public string CallStyle { get; set; } = "SELECT";

    public bool ReturnsTable { get; set; }
    public List<MappingParameter> Parameters { get; set; } = [];
}

public sealed class MappingParameter
{
    public string PostgresName { get; set; } = "";
    public string PostgresType { get; set; } = "";
    public int Position { get; set; }
    public bool HasDefault { get; set; }
}
