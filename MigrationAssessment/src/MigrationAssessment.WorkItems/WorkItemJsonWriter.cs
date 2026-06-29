using System.Text.Json;
using System.Text.Json.Serialization;
using MigrationAssessment.WorkItems.Models;

namespace MigrationAssessment.WorkItems;

/// <summary>
/// Writes work items to a JSON file conforming to the published schema.
/// Uses DTOs with camelCase property naming for JSON serialization.
/// </summary>
public sealed class WorkItemJsonWriter : IWorkItemJsonWriter
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    /// <inheritdoc />
    public async Task<WriteResult> WriteAsync(
        WorkItemResult result,
        string outputPath,
        CancellationToken ct)
    {
        try
        {
            // Create output directory if it does not exist
            var directory = Path.GetDirectoryName(Path.GetFullPath(outputPath));
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            // Map domain models to JSON DTOs
            var outputDto = MapToDto(result);

            // Serialize and write
            var json = JsonSerializer.Serialize(outputDto, JsonOptions);
            await File.WriteAllTextAsync(outputPath, json, ct).ConfigureAwait(false);

            return new WriteResult { Succeeded = true };
        }
        catch (IOException ex)
        {
            return new WriteResult
            {
                Succeeded = false,
                ErrorMessage = $"Failed to write JSON to '{outputPath}': {ex.Message}"
            };
        }
        catch (UnauthorizedAccessException ex)
        {
            return new WriteResult
            {
                Succeeded = false,
                ErrorMessage = $"Failed to write JSON to '{outputPath}': {ex.Message}"
            };
        }
    }

    private static WorkItemOutputDto MapToDto(WorkItemResult result)
    {
        // Order work items by PriorityScore descending
        var orderedItems = result.WorkItems
            .OrderByDescending(wi => wi.PriorityScore)
            .ThenByDescending(wi => wi.RiskLevel)
            .ThenByDescending(wi => wi.AffectedObjects.Sum(ao => ao.StatementCount))
            .ToList();

        return new WorkItemOutputDto
        {
            Metadata = new MetadataDto
            {
                GeneratedAt = result.Metadata.GeneratedAt,
                SourceAssessmentPath = result.Metadata.SourceAssessmentPath,
                TotalWorkItemCount = result.Metadata.TotalWorkItemCount,
                TotalEstimatedEffort = new HourRangeDto
                {
                    MinHours = result.Metadata.TotalEstimatedEffort.MinHours,
                    MaxHours = result.Metadata.TotalEstimatedEffort.MaxHours
                },
                ConfidenceSummary = result.Metadata.ConfidenceSummary is not null
                    ? new ConfidenceSummaryDto
                    {
                        HighConfidenceHours = new HourRangeDto
                        {
                            MinHours = result.Metadata.ConfidenceSummary.HighConfidenceHours.MinHours,
                            MaxHours = result.Metadata.ConfidenceSummary.HighConfidenceHours.MaxHours
                        },
                        MediumConfidenceHours = new HourRangeDto
                        {
                            MinHours = result.Metadata.ConfidenceSummary.MediumConfidenceHours.MinHours,
                            MaxHours = result.Metadata.ConfidenceSummary.MediumConfidenceHours.MaxHours
                        },
                        LowConfidenceHours = new HourRangeDto
                        {
                            MinHours = result.Metadata.ConfidenceSummary.LowConfidenceHours.MinHours,
                            MaxHours = result.Metadata.ConfidenceSummary.LowConfidenceHours.MaxHours
                        },
                        Notes = result.Metadata.ConfidenceSummary.Notes
                    }
                    : null
            },
            ValidationSummary = result.ValidationSummary is not null
                ? new ValidationSummaryDto
                {
                    Passed = result.ValidationSummary.Passed,
                    WarningCount = result.ValidationSummary.WarningCount,
                    Warnings = result.ValidationSummary.Warnings.Select(w => new ValidationWarningDto
                    {
                        WorkItemId = w.WorkItemId,
                        Category = w.Category,
                        Message = w.Message
                    }).ToList()
                }
                : null,
            WorkItems = orderedItems.Select(MapWorkItemToDto).ToList()
        };
    }

    private static WorkItemDto MapWorkItemToDto(WorkItem item) => new()
    {
        Id = item.Id,
        Title = item.Title,
        Description = item.Description,
        SqlServerPattern = item.SqlServerPattern,
        PostgresEquivalent = item.PostgresEquivalent,
        AffectedObjects = item.AffectedObjects.Select(ao => new AffectedObjectDto
        {
            Name = ao.Name,
            Type = ao.Type,
            StatementCount = ao.StatementCount
        }).ToList(),
        RiskLevel = item.RiskLevel,
        Priority = item.Priority,
        PriorityScore = item.PriorityScore,
        EstimatedEffort = new HourRangeDto
        {
            MinHours = item.EstimatedEffort.MinHours,
            MaxHours = item.EstimatedEffort.MaxHours
        },
        ConfidenceLevel = item.ConfidenceLevel.ToString().ToLowerInvariant(),
        AcceptanceCriteria = item.AcceptanceCriteria.ToList(),
        RemediationGuidance = item.RemediationGuidance,
        Tags = item.Tags.ToList(),
        RelatedWorkItemIds = item.RelatedWorkItemIds.Count > 0
            ? item.RelatedWorkItemIds.ToList()
            : null
    };

    #region JSON DTOs

    private sealed class WorkItemOutputDto
    {
        public required MetadataDto Metadata { get; init; }
        public ValidationSummaryDto? ValidationSummary { get; init; }
        public required List<WorkItemDto> WorkItems { get; init; }
    }

    private sealed class MetadataDto
    {
        public required DateTimeOffset GeneratedAt { get; init; }
        public string? SourceAssessmentPath { get; init; }
        public required int TotalWorkItemCount { get; init; }
        public required HourRangeDto TotalEstimatedEffort { get; init; }
        public ConfidenceSummaryDto? ConfidenceSummary { get; init; }
    }

    private sealed class HourRangeDto
    {
        public required double MinHours { get; init; }
        public required double MaxHours { get; init; }
    }

    private sealed class WorkItemDto
    {
        public required string Id { get; init; }
        public required string Title { get; init; }
        public required string Description { get; init; }
        public required string SqlServerPattern { get; init; }
        public required string PostgresEquivalent { get; init; }
        public required List<AffectedObjectDto> AffectedObjects { get; init; }
        public required int RiskLevel { get; init; }
        public required string Priority { get; init; }
        public required double PriorityScore { get; init; }
        public required HourRangeDto EstimatedEffort { get; init; }
        public required string ConfidenceLevel { get; init; }
        public required List<string> AcceptanceCriteria { get; init; }
        public required string RemediationGuidance { get; init; }
        public required List<string> Tags { get; init; }
        public List<string>? RelatedWorkItemIds { get; init; }
    }

    private sealed class ConfidenceSummaryDto
    {
        public required HourRangeDto HighConfidenceHours { get; init; }
        public required HourRangeDto MediumConfidenceHours { get; init; }
        public required HourRangeDto LowConfidenceHours { get; init; }
        public required string Notes { get; init; }
    }

    private sealed class AffectedObjectDto
    {
        public required string Name { get; init; }
        public required string Type { get; init; }
        public required int StatementCount { get; init; }
    }

    private sealed class ValidationSummaryDto
    {
        public required bool Passed { get; init; }
        public required int WarningCount { get; init; }
        public required List<ValidationWarningDto> Warnings { get; init; }
    }

    private sealed class ValidationWarningDto
    {
        public required string WorkItemId { get; init; }
        public required string Category { get; init; }
        public required string Message { get; init; }
    }

    #endregion
}
