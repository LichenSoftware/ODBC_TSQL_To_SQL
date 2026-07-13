using System.Text.Json.Serialization;

namespace SchemaConversion.RuleEngine.Models;

/// <summary>
/// Root configuration model for function-mappings.json deserialization.
/// </summary>
internal sealed class FunctionMappingConfig
{
    [JsonPropertyName("mappings")]
    public List<FunctionMappingEntry> Mappings { get; set; } = [];

    [JsonPropertyName("datePatterns")]
    public List<DatePatternEntry> DatePatterns { get; set; } = [];

    [JsonPropertyName("convertPatterns")]
    public ConvertPatternsEntry? ConvertPatterns { get; set; }

    [JsonPropertyName("castPatterns")]
    public CastPatternsEntry? CastPatterns { get; set; }

    [JsonPropertyName("styleCodes")]
    public Dictionary<string, StyleCodeEntry> StyleCodes { get; set; } = [];
}

/// <summary>
/// A single function mapping entry.
/// </summary>
internal sealed class FunctionMappingEntry
{
    [JsonPropertyName("sqlServerFunction")]
    public string SqlServerFunction { get; set; } = string.Empty;

    [JsonPropertyName("postgresExpression")]
    public string? PostgresExpression { get; set; }

    [JsonPropertyName("argCount")]
    public int ArgCount { get; set; }

    [JsonPropertyName("notes")]
    public string? Notes { get; set; }

    [JsonPropertyName("requiresProceduralContext")]
    public bool RequiresProceduralContext { get; set; }

    [JsonPropertyName("proceduralPattern")]
    public string? ProceduralPattern { get; set; }
}

/// <summary>
/// Date pattern entry for DATEDIFF/DATEADD.
/// </summary>
internal sealed class DatePatternEntry
{
    [JsonPropertyName("sqlServerFunction")]
    public string SqlServerFunction { get; set; } = string.Empty;

    [JsonPropertyName("argCount")]
    public int ArgCount { get; set; }

    [JsonPropertyName("description")]
    public string? Description { get; set; }

    [JsonPropertyName("partMappings")]
    public Dictionary<string, string> PartMappings { get; set; } = [];
}

/// <summary>
/// CONVERT pattern configuration.
/// </summary>
internal sealed class ConvertPatternsEntry
{
    [JsonPropertyName("description")]
    public string? Description { get; set; }

    [JsonPropertyName("typeOnly")]
    public string TypeOnly { get; set; } = string.Empty;

    [JsonPropertyName("withStyleCode")]
    public string WithStyleCode { get; set; } = string.Empty;

    [JsonPropertyName("toDateWithStyleCode")]
    public string ToDateWithStyleCode { get; set; } = string.Empty;

    [JsonPropertyName("toTimestampWithStyleCode")]
    public string ToTimestampWithStyleCode { get; set; } = string.Empty;
}

/// <summary>
/// CAST pattern configuration.
/// </summary>
internal sealed class CastPatternsEntry
{
    [JsonPropertyName("description")]
    public string? Description { get; set; }

    [JsonPropertyName("pattern")]
    public string Pattern { get; set; } = string.Empty;
}

/// <summary>
/// Style code entry for CONVERT date formatting.
/// </summary>
internal sealed class StyleCodeEntry
{
    [JsonPropertyName("format")]
    public string Format { get; set; } = string.Empty;

    [JsonPropertyName("toCharPattern")]
    public string ToCharPattern { get; set; } = string.Empty;
}
