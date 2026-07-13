using System.Text.Json;
using Microsoft.Extensions.Logging;
using SchemaConversion.Core.Interfaces;
using SchemaConversion.Core.Models;
using SchemaConversion.Core.Options;

namespace SchemaConversion.AiEngine;

/// <summary>
/// Implements IAiConverter by coordinating PromptManager, BedrockClient, and AiResponseParser.
/// Writes audit log entries for every attempt and applies confidence threshold checks.
/// </summary>
public sealed class AiConverterService : IAiConverter
{
    private readonly BedrockClient _bedrockClient;
    private readonly PromptManager _promptManager;
    private readonly AiResponseParser _responseParser;
    private readonly IAuditLogWriter _auditLogWriter;
    private readonly ILogger<AiConverterService> _logger;
    private readonly BedrockClientOptions _bedrockOptions;

    private static readonly JsonSerializerOptions ResponseSchemaOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    public AiConverterService(
        BedrockClient bedrockClient,
        PromptManager promptManager,
        AiResponseParser responseParser,
        IAuditLogWriter auditLogWriter,
        BedrockClientOptions bedrockOptions,
        ILogger<AiConverterService> logger)
    {
        _bedrockClient = bedrockClient;
        _promptManager = promptManager;
        _responseParser = responseParser;
        _auditLogWriter = auditLogWriter;
        _bedrockOptions = bedrockOptions;
        _logger = logger;
    }

    /// <summary>
    /// Converts a schema object using AI-assisted conversion via Amazon Bedrock.
    /// </summary>
    public async Task<ConversionResult> ConvertAsync(
        SchemaObject obj, ConversionContext context, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(obj);
        ArgumentNullException.ThrowIfNull(context);

        var templateCategory = ClassifyTemplateCategory(obj.ObjectType);
        var templateVersion = _promptManager.GetTemplateVersion(templateCategory);

        _logger.LogInformation(
            "Starting AI conversion for {Schema}.{Object} ({Type}) using template '{Category}' v{Version}",
            obj.SchemaName, obj.Name, obj.ObjectType, templateCategory, templateVersion);

        var placeholders = BuildPlaceholders(obj, context);
        var fullPrompt = _promptManager.BuildPrompt(templateCategory, placeholders);

        var maxAttempts = _bedrockOptions.MaxRetryAttempts;

        for (var attempt = 0; attempt < maxAttempts; attempt++)
        {
            ct.ThrowIfCancellationRequested();

            string rawResponse;
            try
            {
                rawResponse = await _bedrockClient.InvokeModelAsync(fullPrompt, ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogError(ex, "Bedrock invocation failed for {Object} on attempt {Attempt}",
                    obj.Name, attempt + 1);

                await WriteAuditEntryAsync(
                    context.SessionId, obj, templateVersion, fullPrompt,
                    ex.Message, attempt, isError: true, ct).ConfigureAwait(false);

                // If this was the last attempt, return failure
                if (attempt >= maxAttempts - 1)
                {
                    return CreateFailedResult(obj, ex.Message, templateVersion);
                }

                continue;
            }

            // Write audit entry for this attempt
            await WriteAuditEntryAsync(
                context.SessionId, obj, templateVersion, fullPrompt,
                rawResponse, attempt, isError: false, ct).ConfigureAwait(false);

            // Parse the response
            var parseResult = _responseParser.Parse(rawResponse);

            if (!parseResult.IsSuccess)
            {
                _logger.LogWarning(
                    "Failed to parse AI response for {Object} on attempt {Attempt}: {Error}",
                    obj.Name, attempt + 1, parseResult.ErrorMessage);

                // Malformed response — retry if we have attempts remaining
                if (attempt >= maxAttempts - 1)
                {
                    return CreateFailedResult(obj,
                        $"Response parsing failed after {maxAttempts} attempts: {parseResult.ErrorMessage}",
                        templateVersion);
                }

                continue;
            }

            var parsed = parseResult.Response!;

            // Apply confidence threshold
            var status = parsed.Confidence >= context.ConfidenceThreshold
                ? ConversionStatus.Converted
                : ConversionStatus.Flagged;

            var reviewFlags = new List<ManualReviewFlag>();

            // Add review areas from AI response
            foreach (var area in parsed.ReviewAreas)
            {
                reviewFlags.Add(new ManualReviewFlag
                {
                    Reason = area.Reason,
                    CodeSection = area.CodeSection
                });
            }

            // If confidence is below threshold, add a flag
            if (status == ConversionStatus.Flagged)
            {
                reviewFlags.Add(new ManualReviewFlag
                {
                    Reason = $"AI confidence ({parsed.Confidence:F2}) is below threshold ({context.ConfidenceThreshold:F2})"
                });

                _logger.LogWarning(
                    "Conversion of {Object} flagged: confidence {Confidence} below threshold {Threshold}",
                    obj.Name, parsed.Confidence, context.ConfidenceThreshold);
            }

            var compatibilityNotes = parsed.CompatibilityNotes
                .Select(n => new CompatibilityNote
                {
                    Category = n.Category,
                    Description = n.Description
                })
                .ToList();

            _logger.LogInformation(
                "AI conversion of {Schema}.{Object} completed with confidence {Confidence} — status: {Status}",
                obj.SchemaName, obj.Name, parsed.Confidence, status);

            return new ConversionResult
            {
                ObjectName = obj.Name,
                SchemaName = obj.SchemaName,
                ObjectType = obj.ObjectType,
                Status = status,
                Method = ConversionMethod.AiAssisted,
                GeneratedDdl = parsed.Ddl,
                WrapperDdl = parsed.WrapperDdl,
                ConfidenceScore = parsed.Confidence,
                Assumptions = parsed.Assumptions,
                ReviewFlags = reviewFlags,
                CompatibilityNotes = compatibilityNotes,
                PromptTemplateVersion = templateVersion
            };
        }

        // Should not reach here, but just in case
        return CreateFailedResult(obj, "All retry attempts exhausted.", templateVersion);
    }

    private static string ClassifyTemplateCategory(SchemaObjectType objectType)
    {
        return objectType switch
        {
            SchemaObjectType.StoredProcedure => "stored-procedure",
            SchemaObjectType.Function => "function",
            SchemaObjectType.Trigger => "trigger",
            SchemaObjectType.View => "view",
            _ => "complex-object"
        };
    }

    private static Dictionary<string, string> BuildPlaceholders(SchemaObject obj, ConversionContext context)
    {
        var typeMappingContext = BuildTypeMappingContext(context);
        var responseSchema = GetResponseSchema();

        return new Dictionary<string, string>
        {
            ["source_definition"] = obj.SourceDefinition,
            ["type_mapping_context"] = typeMappingContext,
            ["response_schema"] = responseSchema
        };
    }

    private static string BuildTypeMappingContext(ConversionContext context)
    {
        if (context.TypeMappings.Count == 0)
            return "No specific type mappings provided. Use standard SQL Server to PostgreSQL type conversions.";

        var mappings = context.TypeMappings
            .Select(m => $"- {m.SqlServerType} → {m.PostgresType}" +
                (string.IsNullOrEmpty(m.AdditionalConstraint) ? "" : $" (constraint: {m.AdditionalConstraint})"))
            .ToList();

        return string.Join("\n", mappings);
    }

    private static string GetResponseSchema()
    {
        var schema = new
        {
            ddl = "string (required) - The converted PostgreSQL DDL",
            wrapperDdl = "string|null - Wrapper DDL if interface changes are needed",
            confidence = "number (required, 0.0-1.0) - Confidence in the conversion accuracy",
            assumptions = "string[] (required) - Assumptions made during conversion",
            reviewAreas = new[]
            {
                new
                {
                    codeSection = "string - The relevant code section",
                    reason = "string - Why manual review is recommended"
                }
            },
            compatibilityNotes = new[]
            {
                new
                {
                    category = "string - Category of the compatibility concern",
                    description = "string - Description of the behavioral difference"
                }
            }
        };

        return JsonSerializer.Serialize(schema, ResponseSchemaOptions);
    }

    private async Task WriteAuditEntryAsync(
        string sessionId,
        SchemaObject obj,
        string templateVersion,
        string fullPrompt,
        string fullResponse,
        int attempt,
        bool isError,
        CancellationToken ct)
    {
        var entry = new AuditLogEntry
        {
            SessionId = sessionId,
            ObjectName = $"{obj.SchemaName}.{obj.Name}",
            ObjectType = obj.ObjectType,
            PromptTemplateVersion = templateVersion,
            FullPrompt = fullPrompt,
            ModelId = _bedrockOptions.ModelId,
            FullResponse = fullResponse,
            Timestamp = DateTimeOffset.UtcNow,
            RetryAttempt = attempt,
            IsError = isError
        };

        try
        {
            await _auditLogWriter.WriteAsync(entry, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            // Audit write failure should not prevent conversion from succeeding
            _logger.LogError(ex, "Failed to write audit log entry for {Object}", obj.Name);
        }
    }

    private static ConversionResult CreateFailedResult(
        SchemaObject obj, string errorMessage, string templateVersion)
    {
        return new ConversionResult
        {
            ObjectName = obj.Name,
            SchemaName = obj.SchemaName,
            ObjectType = obj.ObjectType,
            Status = ConversionStatus.Failed,
            Method = ConversionMethod.AiAssisted,
            ErrorMessage = errorMessage,
            PromptTemplateVersion = templateVersion,
            ReviewFlags =
            [
                new ManualReviewFlag
                {
                    Reason = $"AI conversion failed: {errorMessage}"
                }
            ]
        };
    }
}
