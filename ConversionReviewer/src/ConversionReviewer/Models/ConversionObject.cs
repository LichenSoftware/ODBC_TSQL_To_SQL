using System.Text.Json.Serialization;

namespace ConversionReviewer.Models;

/// <summary>
/// Root model matching the JSON structure of each converted object file.
/// </summary>
public class ConversionObject
{
    [JsonPropertyName("source")]
    public SourceInfo Source { get; set; } = new();

    [JsonPropertyName("result")]
    public ConversionResult Result { get; set; } = new();

    [JsonPropertyName("convertedAt")]
    public DateTimeOffset ConvertedAt { get; set; }

    [JsonPropertyName("isManuallyEdited")]
    public bool IsManuallyEdited { get; set; }

    // Tracking fields (not in original JSON, managed by reviewer)
    [JsonPropertyName("appliedAt")]
    public DateTimeOffset? AppliedAt { get; set; }

    [JsonPropertyName("appliedSuccessfully")]
    public bool? AppliedSuccessfully { get; set; }

    [JsonPropertyName("applyError")]
    public string? ApplyError { get; set; }

    // Computed helpers (not serialized)
    [JsonIgnore]
    public string FileName { get; set; } = string.Empty;

    [JsonIgnore]
    public string FullPath { get; set; } = string.Empty;

    [JsonIgnore]
    public string DisplayName => $"{Source.SchemaName}.{Source.Name}";

    [JsonIgnore]
    public bool IsApplied => AppliedAt.HasValue && AppliedSuccessfully == true;
}

public class SourceInfo
{
    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("schemaName")]
    public string SchemaName { get; set; } = string.Empty;

    [JsonPropertyName("objectType")]
    public string ObjectType { get; set; } = string.Empty;

    [JsonPropertyName("sourceDefinition")]
    public string SourceDefinition { get; set; } = string.Empty;

    [JsonPropertyName("sourceDefinitionHash")]
    public string SourceDefinitionHash { get; set; } = string.Empty;

    [JsonPropertyName("dependsOn")]
    public List<string> DependsOn { get; set; } = [];
}

public class ConversionResult
{
    [JsonPropertyName("objectName")]
    public string ObjectName { get; set; } = string.Empty;

    [JsonPropertyName("schemaName")]
    public string SchemaName { get; set; } = string.Empty;

    [JsonPropertyName("objectType")]
    public string ObjectType { get; set; } = string.Empty;

    [JsonPropertyName("status")]
    public string Status { get; set; } = string.Empty;

    [JsonPropertyName("method")]
    public string Method { get; set; } = string.Empty;

    [JsonPropertyName("generatedDdl")]
    public string GeneratedDdl { get; set; } = string.Empty;

    [JsonPropertyName("confidenceScore")]
    public double ConfidenceScore { get; set; }

    [JsonPropertyName("assumptions")]
    public List<string> Assumptions { get; set; } = [];

    [JsonPropertyName("reviewFlags")]
    public List<ReviewFlag> ReviewFlags { get; set; } = [];

    [JsonPropertyName("compatibilityNotes")]
    public List<CompatibilityNote> CompatibilityNotes { get; set; } = [];

    [JsonPropertyName("promptTemplateVersion")]
    public string? PromptTemplateVersion { get; set; }
}

public class ReviewFlag
{
    [JsonPropertyName("reason")]
    public string Reason { get; set; } = string.Empty;

    [JsonPropertyName("codeSection")]
    public string CodeSection { get; set; } = string.Empty;
}

public class CompatibilityNote
{
    [JsonPropertyName("category")]
    public string Category { get; set; } = string.Empty;

    [JsonPropertyName("description")]
    public string Description { get; set; } = string.Empty;
}
