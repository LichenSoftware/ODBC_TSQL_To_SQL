using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;

namespace SchemaConversion.AiEngine;

/// <summary>
/// Parses and validates the structured JSON response from the AI model.
/// </summary>
public sealed class AiResponseParser
{
    private readonly ILogger<AiResponseParser> _logger;

    private static readonly JsonSerializerOptions DeserializeOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public AiResponseParser(ILogger<AiResponseParser> logger)
    {
        _logger = logger;
    }

    /// <summary>
    /// Parses the raw AI response text into a structured result.
    /// Returns a failure result if the response is malformed or missing required fields.
    /// </summary>
    public AiParseResult Parse(string rawResponse)
    {
        if (string.IsNullOrWhiteSpace(rawResponse))
        {
            _logger.LogWarning("Received empty response from AI model");
            return AiParseResult.Failure("Response is empty or whitespace.");
        }

        // Try to extract JSON from the response (model may include markdown code fences)
        var json = ExtractJson(rawResponse);

        if (string.IsNullOrWhiteSpace(json))
        {
            _logger.LogWarning("Could not extract JSON from AI response");
            return AiParseResult.Failure("Response does not contain valid JSON.");
        }

        AiResponseDto? dto;
        try
        {
            dto = JsonSerializer.Deserialize<AiResponseDto>(json, DeserializeOptions);
        }
        catch (JsonException ex)
        {
            _logger.LogWarning(ex, "Failed to deserialize AI response JSON");
            return AiParseResult.Failure($"JSON deserialization failed: {ex.Message}");
        }

        if (dto is null)
        {
            return AiParseResult.Failure("Deserialized response is null.");
        }

        // Validate required fields
        var validationErrors = ValidateResponse(dto);
        if (validationErrors.Count > 0)
        {
            var errorMessage = string.Join("; ", validationErrors);
            _logger.LogWarning("AI response validation failed: {Errors}", errorMessage);
            return AiParseResult.Failure($"Response validation failed: {errorMessage}");
        }

        _logger.LogDebug("Successfully parsed AI response with confidence {Confidence}", dto.Confidence);

        return AiParseResult.Success(new AiParsedResponse
        {
            Ddl = dto.Ddl!,
            WrapperDdl = dto.WrapperDdl,
            Confidence = dto.Confidence!.Value,
            Assumptions = dto.Assumptions ?? [],
            ReviewAreas = dto.ReviewAreas?.Select(r => new ParsedReviewArea
            {
                CodeSection = r.CodeSection ?? string.Empty,
                Reason = r.Reason ?? string.Empty
            }).ToList() ?? [],
            CompatibilityNotes = dto.CompatibilityNotes?.Select(n => new ParsedCompatibilityNote
            {
                Category = n.Category ?? string.Empty,
                Description = n.Description ?? string.Empty
            }).ToList() ?? []
        });
    }

    private static string? ExtractJson(string text)
    {
        var trimmed = text.Trim();

        // If it starts with {, assume it's raw JSON
        if (trimmed.StartsWith('{'))
        {
            return trimmed;
        }

        // Try to extract from markdown code fences
        const string jsonFenceStart = "```json";
        const string fenceStart = "```";

        var startIndex = trimmed.IndexOf(jsonFenceStart, StringComparison.OrdinalIgnoreCase);
        if (startIndex >= 0)
        {
            startIndex = trimmed.IndexOf('\n', startIndex) + 1;
        }
        else
        {
            startIndex = trimmed.IndexOf(fenceStart, StringComparison.Ordinal);
            if (startIndex >= 0)
            {
                startIndex = trimmed.IndexOf('\n', startIndex) + 1;
            }
        }

        if (startIndex <= 0)
        {
            // Last resort: find the first { and last }
            var firstBrace = trimmed.IndexOf('{');
            var lastBrace = trimmed.LastIndexOf('}');
            if (firstBrace >= 0 && lastBrace > firstBrace)
            {
                return trimmed[firstBrace..(lastBrace + 1)];
            }
            return null;
        }

        var endIndex = trimmed.IndexOf("```", startIndex, StringComparison.Ordinal);
        if (endIndex < 0)
        {
            endIndex = trimmed.Length;
        }

        return trimmed[startIndex..endIndex].Trim();
    }

    private static List<string> ValidateResponse(AiResponseDto dto)
    {
        var errors = new List<string>();

        if (string.IsNullOrWhiteSpace(dto.Ddl))
            errors.Add("'ddl' is required and must be non-empty");

        if (dto.Confidence is null)
            errors.Add("'confidence' is required");
        else if (dto.Confidence < 0.0 || dto.Confidence > 1.0)
            errors.Add("'confidence' must be between 0.0 and 1.0");

        if (dto.Assumptions is null)
            errors.Add("'assumptions' array is required");

        if (dto.ReviewAreas is null)
            errors.Add("'reviewAreas' array is required");

        return errors;
    }

    // Internal DTO for deserialization
    private sealed class AiResponseDto
    {
        public string? Ddl { get; set; }
        public string? WrapperDdl { get; set; }
        public double? Confidence { get; set; }
        public List<string>? Assumptions { get; set; }
        public List<ReviewAreaDto>? ReviewAreas { get; set; }
        public List<CompatibilityNoteDto>? CompatibilityNotes { get; set; }
    }

    private sealed class ReviewAreaDto
    {
        public string? CodeSection { get; set; }
        public string? Reason { get; set; }
    }

    private sealed class CompatibilityNoteDto
    {
        public string? Category { get; set; }
        public string? Description { get; set; }
    }
}

/// <summary>
/// Result of parsing an AI response. Either contains a parsed response or a failure reason.
/// </summary>
public sealed class AiParseResult
{
    public bool IsSuccess { get; private init; }
    public AiParsedResponse? Response { get; private init; }
    public string? ErrorMessage { get; private init; }

    public static AiParseResult Success(AiParsedResponse response) => new()
    {
        IsSuccess = true,
        Response = response
    };

    public static AiParseResult Failure(string errorMessage) => new()
    {
        IsSuccess = false,
        ErrorMessage = errorMessage
    };
}

/// <summary>
/// Parsed and validated AI response data.
/// </summary>
public sealed class AiParsedResponse
{
    public required string Ddl { get; init; }
    public string? WrapperDdl { get; init; }
    public required double Confidence { get; init; }
    public required IReadOnlyList<string> Assumptions { get; init; }
    public required IReadOnlyList<ParsedReviewArea> ReviewAreas { get; init; }
    public required IReadOnlyList<ParsedCompatibilityNote> CompatibilityNotes { get; init; }
}

public sealed class ParsedReviewArea
{
    public required string CodeSection { get; init; }
    public required string Reason { get; init; }
}

public sealed class ParsedCompatibilityNote
{
    public required string Category { get; init; }
    public required string Description { get; init; }
}
