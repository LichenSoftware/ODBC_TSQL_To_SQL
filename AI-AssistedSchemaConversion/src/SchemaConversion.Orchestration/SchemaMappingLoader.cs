using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace SchemaConversion.Orchestration;

/// <summary>
/// Loads schema mappings from the schema-mappings.json configuration file.
/// </summary>
public sealed class SchemaMappingLoader
{
    private readonly IReadOnlyDictionary<string, string> _mappings;
    private readonly ILogger<SchemaMappingLoader> _logger;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    public SchemaMappingLoader(string configFilePath, ILogger<SchemaMappingLoader> logger)
    {
        _logger = logger;
        _mappings = LoadMappings(configFilePath);
        _logger.LogInformation("SchemaMappingLoader loaded {Count} schema mappings from {Path}",
            _mappings.Count, configFilePath);
    }

    /// <summary>
    /// Gets the loaded schema mappings (source schema → target schema).
    /// </summary>
    public IReadOnlyDictionary<string, string> GetMappings() => _mappings;

    private IReadOnlyDictionary<string, string> LoadMappings(string configFilePath)
    {
        if (!File.Exists(configFilePath))
        {
            _logger.LogWarning("Schema mappings file not found at {Path}. No schema mappings will be applied.",
                configFilePath);
            return new Dictionary<string, string>();
        }

        try
        {
            var json = File.ReadAllText(configFilePath);
            var config = JsonSerializer.Deserialize<SchemaMappingConfig>(json, JsonOptions);

            if (config?.DefaultMappings is null || config.DefaultMappings.Count == 0)
            {
                _logger.LogWarning("No defaultMappings found in schema mappings config.");
                return new Dictionary<string, string>();
            }

            var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var mapping in config.DefaultMappings)
            {
                if (!string.IsNullOrWhiteSpace(mapping.SqlServerSchema) &&
                    !string.IsNullOrWhiteSpace(mapping.PostgresSchema))
                {
                    result[mapping.SqlServerSchema] = mapping.PostgresSchema;
                }
            }

            return result;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to load schema mappings from {Path}", configFilePath);
            return new Dictionary<string, string>();
        }
    }

    private sealed class SchemaMappingConfig
    {
        public List<SchemaMappingEntry>? DefaultMappings { get; set; }
    }

    private sealed class SchemaMappingEntry
    {
        public string? SqlServerSchema { get; set; }
        public string? PostgresSchema { get; set; }
        public string? Notes { get; set; }
    }
}
