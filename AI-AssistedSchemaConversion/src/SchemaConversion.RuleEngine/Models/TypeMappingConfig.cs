using System.Text.Json.Serialization;

namespace SchemaConversion.RuleEngine.Models;

/// <summary>
/// Root configuration model for type-mappings.json deserialization.
/// </summary>
internal sealed class TypeMappingConfig
{
    [JsonPropertyName("mappings")]
    public List<TypeMappingEntry> Mappings { get; set; } = [];
}

/// <summary>
/// A single entry in the type-mappings.json file.
/// </summary>
internal sealed class TypeMappingEntry
{
    [JsonPropertyName("sqlServerType")]
    public string SqlServerType { get; set; } = string.Empty;

    [JsonPropertyName("postgresType")]
    public string? PostgresType { get; set; }

    [JsonPropertyName("preservePrecision")]
    public bool PreservePrecision { get; set; }

    [JsonPropertyName("maxPrecision")]
    public int? MaxPrecision { get; set; }

    [JsonPropertyName("maxLengthMapping")]
    public string? MaxLengthMapping { get; set; }

    [JsonPropertyName("additionalConstraint")]
    public string? AdditionalConstraint { get; set; }

    [JsonPropertyName("requiresManualReview")]
    public bool RequiresManualReview { get; set; }

    [JsonPropertyName("compatibilityNote")]
    public string? CompatibilityNote { get; set; }
}
