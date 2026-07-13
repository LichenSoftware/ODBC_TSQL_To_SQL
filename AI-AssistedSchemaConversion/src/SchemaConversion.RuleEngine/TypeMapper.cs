using System.Text.Json;
using Microsoft.Extensions.Logging;
using SchemaConversion.RuleEngine.Models;

namespace SchemaConversion.RuleEngine;

/// <summary>
/// Maps SQL Server data types to PostgreSQL equivalents using configuration from type-mappings.json.
/// Handles precision/scale propagation, maxPrecision capping, and additional constraint generation.
/// </summary>
public sealed class TypeMapper
{
    private readonly Dictionary<string, TypeMappingEntry> _mappings;
    private readonly ILogger<TypeMapper> _logger;

    /// <summary>
    /// Initializes a new instance of <see cref="TypeMapper"/> by loading mappings from the specified JSON file.
    /// </summary>
    /// <param name="configFilePath">Path to the type-mappings.json configuration file.</param>
    /// <param name="logger">Logger instance for diagnostics.</param>
    /// <exception cref="InvalidOperationException">Thrown when the configuration file is missing, malformed, or contains invalid entries.</exception>
    public TypeMapper(string configFilePath, ILogger<TypeMapper> logger)
    {
        _logger = logger;
        _mappings = LoadAndValidateMappings(configFilePath);
        _logger.LogInformation("TypeMapper loaded {Count} type mappings from {Path}", _mappings.Count, configFilePath);
    }

    /// <summary>
    /// Maps a SQL Server type to its PostgreSQL equivalent.
    /// </summary>
    /// <param name="sqlServerType">The SQL Server type name (e.g., "INT", "NVARCHAR", "DATETIME2").</param>
    /// <param name="precision">Optional precision value for numeric/datetime types.</param>
    /// <param name="scale">Optional scale value for numeric types.</param>
    /// <param name="length">Optional length for string/binary types. Use -1 for MAX.</param>
    /// <returns>A <see cref="TypeMappingResult"/> containing the mapped type and any additional constraints.</returns>
    public TypeMappingResult MapType(string sqlServerType, int? precision = null, int? scale = null, int? length = null)
    {
        var normalizedType = sqlServerType.Trim().ToUpperInvariant();

        if (!_mappings.TryGetValue(normalizedType, out var entry))
        {
            _logger.LogWarning("No mapping found for SQL Server type: {Type}", sqlServerType);
            return new TypeMappingResult
            {
                MappedType = null,
                RequiresManualReview = true,
                CompatibilityNote = $"No mapping configured for SQL Server type '{sqlServerType}'."
            };
        }

        if (entry.RequiresManualReview)
        {
            return new TypeMappingResult
            {
                MappedType = entry.PostgresType,
                RequiresManualReview = true,
                CompatibilityNote = entry.CompatibilityNote
            };
        }

        if (entry.PostgresType is null)
        {
            return new TypeMappingResult
            {
                MappedType = null,
                RequiresManualReview = true,
                CompatibilityNote = entry.CompatibilityNote ?? $"No PostgreSQL equivalent for '{sqlServerType}'."
            };
        }

        // Handle MAX length mapping (VARCHAR(MAX) / NVARCHAR(MAX) → TEXT)
        if (length == -1 && entry.MaxLengthMapping is not null)
        {
            return new TypeMappingResult
            {
                MappedType = entry.MaxLengthMapping,
                AdditionalConstraint = entry.AdditionalConstraint
            };
        }

        var mappedType = entry.PostgresType;

        if (entry.PreservePrecision)
        {
            mappedType = ApplyPrecisionAndScale(mappedType, precision, scale, length, entry.MaxPrecision);
        }

        return new TypeMappingResult
        {
            MappedType = mappedType,
            AdditionalConstraint = entry.AdditionalConstraint
        };
    }

    private static string ApplyPrecisionAndScale(string template, int? precision, int? scale, int? length, int? maxPrecision)
    {
        // Apply maxPrecision cap
        var effectivePrecision = precision;
        if (effectivePrecision.HasValue && maxPrecision.HasValue && effectivePrecision.Value > maxPrecision.Value)
        {
            effectivePrecision = maxPrecision.Value;
        }

        var result = template;

        // Replace precision placeholder
        if (result.Contains("{precision}"))
        {
            var precValue = effectivePrecision?.ToString() ?? "0";
            result = result.Replace("{precision}", precValue);
        }

        // Replace scale placeholder
        if (result.Contains("{scale}"))
        {
            var scaleValue = scale?.ToString() ?? "0";
            result = result.Replace("{scale}", scaleValue);
        }

        // Replace length placeholder
        if (result.Contains("{length}"))
        {
            var lengthValue = length?.ToString() ?? "255";
            result = result.Replace("{length}", lengthValue);
        }

        return result;
    }

    private Dictionary<string, TypeMappingEntry> LoadAndValidateMappings(string configFilePath)
    {
        if (!File.Exists(configFilePath))
        {
            throw new InvalidOperationException($"Type mappings configuration file not found: {configFilePath}");
        }

        string json;
        try
        {
            json = File.ReadAllText(configFilePath);
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException($"Failed to read type mappings configuration file: {configFilePath}", ex);
        }

        TypeMappingConfig? config;
        try
        {
            config = JsonSerializer.Deserialize<TypeMappingConfig>(json);
        }
        catch (JsonException ex)
        {
            throw new InvalidOperationException($"Type mappings configuration file is not valid JSON: {configFilePath}", ex);
        }

        if (config?.Mappings is null || config.Mappings.Count == 0)
        {
            throw new InvalidOperationException($"Type mappings configuration file contains no mappings: {configFilePath}");
        }

        var dictionary = new Dictionary<string, TypeMappingEntry>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in config.Mappings)
        {
            if (string.IsNullOrWhiteSpace(entry.SqlServerType))
            {
                throw new InvalidOperationException("Type mapping entry is missing required field 'sqlServerType'.");
            }

            // postgresType can be null for types that require manual review
            if (!entry.RequiresManualReview && entry.PostgresType is null)
            {
                throw new InvalidOperationException(
                    $"Type mapping entry for '{entry.SqlServerType}' is missing required field 'postgresType' " +
                    "and is not marked as requiring manual review.");
            }

            dictionary[entry.SqlServerType.ToUpperInvariant()] = entry;
        }

        return dictionary;
    }
}
