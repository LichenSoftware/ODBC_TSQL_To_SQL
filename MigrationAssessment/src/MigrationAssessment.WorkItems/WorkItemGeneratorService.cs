using MigrationAssessment.Core.Models;
using MigrationAssessment.WorkItems.Models;

namespace MigrationAssessment.WorkItems;

/// <summary>
/// Main orchestrator that wires all work item generation components together.
/// Implements the pipeline integration entry point (in-memory data) and
/// standalone file-based entry point.
/// </summary>
public sealed class WorkItemGeneratorService : IWorkItemGenerator
{
    private readonly IStatementGrouper _grouper;
    private readonly IPriorityCalculator _priorityCalculator;
    private readonly IEffortEstimator _effortEstimator;
    private readonly IRemediationKnowledgeBase _knowledgeBase;
    private readonly IPostgresConversionEngine _conversionEngine;
    private readonly WorkItemDeduplicator _deduplicator;
    private readonly TitleGenerator _titleGenerator;
    private readonly DescriptionGenerator _descriptionGenerator;
    private readonly RemediationGuidanceGenerator _guidanceGenerator;
    private readonly AcceptanceCriteriaGenerator _acceptanceCriteriaGenerator;
    private readonly AssessmentJsonReader _jsonReader;
    private readonly IWorkItemJsonWriter _jsonWriter;
    private readonly IWorkItemMarkdownWriter _markdownWriter;

    public WorkItemGeneratorService(
        IStatementGrouper grouper,
        IPriorityCalculator priorityCalculator,
        IEffortEstimator effortEstimator,
        IRemediationKnowledgeBase knowledgeBase,
        IPostgresConversionEngine conversionEngine,
        WorkItemDeduplicator deduplicator,
        TitleGenerator titleGenerator,
        DescriptionGenerator descriptionGenerator,
        RemediationGuidanceGenerator guidanceGenerator,
        AcceptanceCriteriaGenerator acceptanceCriteriaGenerator,
        AssessmentJsonReader jsonReader,
        IWorkItemJsonWriter jsonWriter,
        IWorkItemMarkdownWriter markdownWriter)
    {
        ArgumentNullException.ThrowIfNull(grouper);
        ArgumentNullException.ThrowIfNull(priorityCalculator);
        ArgumentNullException.ThrowIfNull(effortEstimator);
        ArgumentNullException.ThrowIfNull(knowledgeBase);
        ArgumentNullException.ThrowIfNull(conversionEngine);
        ArgumentNullException.ThrowIfNull(deduplicator);
        ArgumentNullException.ThrowIfNull(titleGenerator);
        ArgumentNullException.ThrowIfNull(descriptionGenerator);
        ArgumentNullException.ThrowIfNull(guidanceGenerator);
        ArgumentNullException.ThrowIfNull(acceptanceCriteriaGenerator);
        ArgumentNullException.ThrowIfNull(jsonReader);
        ArgumentNullException.ThrowIfNull(jsonWriter);
        ArgumentNullException.ThrowIfNull(markdownWriter);

        _grouper = grouper;
        _priorityCalculator = priorityCalculator;
        _effortEstimator = effortEstimator;
        _knowledgeBase = knowledgeBase;
        _conversionEngine = conversionEngine;
        _deduplicator = deduplicator;
        _titleGenerator = titleGenerator;
        _descriptionGenerator = descriptionGenerator;
        _guidanceGenerator = guidanceGenerator;
        _acceptanceCriteriaGenerator = acceptanceCriteriaGenerator;
        _jsonReader = jsonReader;
        _jsonWriter = jsonWriter;
        _markdownWriter = markdownWriter;
    }

    /// <inheritdoc />
    public WorkItemResult GenerateWorkItems(
        IReadOnlyList<AnalyzedStatement> statements,
        FeatureDetectionResult featureDetection,
        WorkItemConfiguration config)
    {
        return GenerateWorkItems(statements, featureDetection, config, Array.Empty<ObjectInventoryEntry>());
    }

    /// <inheritdoc />
    public WorkItemResult GenerateWorkItems(
        IReadOnlyList<AnalyzedStatement> statements,
        FeatureDetectionResult featureDetection,
        WorkItemConfiguration config,
        IReadOnlyList<ObjectInventoryEntry> objectInventory)
    {
        return GenerateWorkItemsInternal(statements, featureDetection, config, objectInventory, null);
    }

    /// <inheritdoc />
    public WorkItemResult GenerateWorkItems(
        IReadOnlyList<AnalyzedStatement> statements,
        FeatureDetectionResult featureDetection,
        WorkItemConfiguration config,
        IReadOnlyList<ObjectInventoryEntry> objectInventory,
        DatabaseObjectInventory rawObjectInventory)
    {
        return GenerateWorkItemsInternal(statements, featureDetection, config, objectInventory, rawObjectInventory);
    }

    private WorkItemResult GenerateWorkItemsInternal(
        IReadOnlyList<AnalyzedStatement> statements,
        FeatureDetectionResult featureDetection,
        WorkItemConfiguration config,
        IReadOnlyList<ObjectInventoryEntry> objectInventory,
        DatabaseObjectInventory? rawObjectInventory)
    {
        ArgumentNullException.ThrowIfNull(statements);
        ArgumentNullException.ThrowIfNull(featureDetection);
        ArgumentNullException.ThrowIfNull(config);

        // 1. Validate configuration
        var validationError = ValidateConfiguration(config);
        if (validationError is not null)
        {
            return CreateFailedResult(validationError);
        }

        // 2. Group statements by feature + database object
        IReadOnlyList<StatementGroup> groups;
        if (objectInventory.Count > 0 && rawObjectInventory is not null)
        {
            groups = _grouper.GroupStatements(statements, featureDetection, config.MinimumRiskLevel, objectInventory, rawObjectInventory);
        }
        else if (objectInventory.Count > 0)
        {
            groups = _grouper.GroupStatements(statements, featureDetection, config.MinimumRiskLevel, objectInventory);
        }
        else
        {
            groups = _grouper.GroupStatements(statements, featureDetection, config.MinimumRiskLevel);
        }

        // 3. Deduplicate groups
        var deduplicatedGroups = _deduplicator.Deduplicate(groups);

        // Handle empty result
        if (deduplicatedGroups.Count == 0)
        {
            return CreateEmptyResult();
        }

        // 4. Build work items from deduplicated groups
        var workItems = new List<WorkItem>(deduplicatedGroups.Count);

        foreach (var dedupGroup in deduplicatedGroups)
        {
            var workItem = BuildWorkItem(dedupGroup);
            workItems.Add(workItem);
        }

        // 5. Sort by priority score descending (with tie-breaking)
        workItems.Sort(CompareByPriorityDescending);

        // 6. Assign priority labels
        var labeledItems = _priorityCalculator.AssignPriorityLabels(workItems);
        workItems = labeledItems
            .Select(pair => pair.Item with { Priority = pair.Priority })
            .ToList();

        // 7. Apply max work item count limit
        if (config.MaxWorkItemCount.HasValue && workItems.Count > config.MaxWorkItemCount.Value)
        {
            workItems = workItems.Take(config.MaxWorkItemCount.Value).ToList();
        }

        // 8. Build metadata and result
        var totalEffort = _effortEstimator.CalculateTotalEffort(workItems);
        var confidenceSummary = _effortEstimator.BuildConfidenceSummary(workItems);

        // 9. Run validation gate
        var validator = new WorkItemValidator();
        var validationSummary = validator.Validate(workItems, objectInventory);

        var metadata = new WorkItemMetadata
        {
            GeneratedAt = DateTimeOffset.UtcNow,
            SourceAssessmentPath = null, // In-memory pipeline mode
            TotalWorkItemCount = workItems.Count,
            TotalEstimatedEffort = totalEffort,
            ConfidenceSummary = confidenceSummary
        };

        return new WorkItemResult
        {
            WorkItems = workItems,
            Metadata = metadata,
            Succeeded = true,
            ValidationSummary = validationSummary
        };
    }

    /// <inheritdoc />
    public async Task<WorkItemResult> GenerateFromFileAsync(
        string assessmentJsonPath,
        WorkItemConfiguration config,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(assessmentJsonPath);
        ArgumentNullException.ThrowIfNull(config);

        // 1. Validate configuration first
        var validationError = ValidateConfiguration(config);
        if (validationError is not null)
        {
            return CreateFailedResult(validationError);
        }

        // 2. Read the assessment JSON file
        var readResult = await _jsonReader.ReadAsync(assessmentJsonPath, ct).ConfigureAwait(false);

        if (!readResult.Succeeded)
        {
            return CreateFailedResult(readResult.ErrorMessage ?? "Failed to read assessment file.");
        }

        // 3. Generate work items from parsed data
        var result = GenerateWorkItems(
            readResult.Statements ?? [],
            readResult.FeatureDetection ?? new FeatureDetectionResult
            {
                FeatureCounts = new Dictionary<string, int>(),
                DetailedInventory = [],
                InaccessibleFeatures = []
            },
            config,
            readResult.ObjectInventory ?? []);

        // 4. Set SourceAssessmentPath in metadata
        result = result with
        {
            Metadata = result.Metadata with
            {
                SourceAssessmentPath = assessmentJsonPath
            }
        };

        // 5. Write JSON output
        var jsonWriteResult = await _jsonWriter.WriteAsync(result, config.OutputJsonPath, ct).ConfigureAwait(false);
        if (!jsonWriteResult.Succeeded)
        {
            return result with
            {
                Succeeded = false,
                ErrorMessage = jsonWriteResult.ErrorMessage
            };
        }

        // 6. Write Markdown output if enabled
        if (config.MarkdownEnabled)
        {
            var markdownPath = ResolveMarkdownPath(config);
            var mdWriteResult = await _markdownWriter.WriteAsync(result, markdownPath, ct).ConfigureAwait(false);
            if (!mdWriteResult.Succeeded)
            {
                return result with
                {
                    Succeeded = false,
                    ErrorMessage = mdWriteResult.ErrorMessage
                };
            }
        }

        return result;
    }

    /// <summary>
    /// Resolves the Markdown output path. If not specified, defaults to the same
    /// directory as the JSON output with filename "work-items.md".
    /// </summary>
    private static string ResolveMarkdownPath(WorkItemConfiguration config)
    {
        if (!string.IsNullOrWhiteSpace(config.MarkdownOutputPath))
        {
            return config.MarkdownOutputPath;
        }

        var jsonDirectory = Path.GetDirectoryName(Path.GetFullPath(config.OutputJsonPath));
        if (string.IsNullOrEmpty(jsonDirectory))
        {
            jsonDirectory = ".";
        }

        return Path.Combine(jsonDirectory, "work-items.md");
    }

    private WorkItem BuildWorkItem(DeduplicatedGroup dedupGroup)
    {
        var group = dedupGroup.Group;
        var featureName = group.FeatureName;
        var objectName = group.DatabaseObjectName;
        var riskLevel = group.MaxRiskLevel;
        var resolvedObjectName = objectName ?? "Ad Hoc Queries";
        var detectedFeatures = group.DetectedFeatures;

        // Generate title using multi-feature overload
        var title = _titleGenerator.GenerateTitle(detectedFeatures, objectName, riskLevel);

        // Generate description using multi-feature overload
        var occurrenceCount = group.Statements.Count;
        var totalExecutionCount = group.Statements.Sum(s => s.Source.ExecutionCount);
        var description = _descriptionGenerator.GenerateDescription(
            detectedFeatures, riskLevel, occurrenceCount, totalExecutionCount, objectName);

        // Generate remediation guidance using multi-feature overload
        var (guidance, requiresResearch) = _guidanceGenerator.GenerateGuidance(
            detectedFeatures, dedupGroup.PrimarySqlPattern);

        // Get PostgreSQL equivalent via conversion engine (full SQL conversion)
        var postgresEquivalent = _conversionEngine.Convert(dedupGroup.PrimarySqlPattern, detectedFeatures);

        // Generate acceptance criteria
        var acceptanceCriteria = _acceptanceCriteriaGenerator.GenerateCriteria(
            featureName, riskLevel, resolvedObjectName);

        // Estimate effort using multi-feature overload
        var effort = _effortEstimator.EstimateEffort(detectedFeatures, occurrenceCount);

        // Derive confidence level from the work item's max risk
        var confidenceLevel = _effortEstimator.DeriveConfidenceLevel(riskLevel);

        // Build tags
        var tags = BuildTags(riskLevel, group, requiresResearch);

        return new WorkItem
        {
            Id = dedupGroup.Id,
            Title = title,
            Description = description,
            SqlServerPattern = dedupGroup.PrimarySqlPattern,
            PostgresEquivalent = postgresEquivalent,
            AffectedObjects = dedupGroup.AffectedObjects,
            RiskLevel = riskLevel,
            Priority = "Medium", // Placeholder — will be overwritten by AssignPriorityLabels
            PriorityScore = dedupGroup.CombinedPriorityScore,
            EstimatedEffort = effort,
            ConfidenceLevel = confidenceLevel,
            AcceptanceCriteria = acceptanceCriteria,
            RemediationGuidance = guidance,
            Tags = tags,
            RelatedWorkItemIds = dedupGroup.RelatedWorkItemIds,
            DetectedFeatures = detectedFeatures,
            RelatedFeatures = detectedFeatures.Distinct().ToList()
        };
    }

    private static IReadOnlyList<string> BuildTags(int riskLevel, StatementGroup group, bool requiresResearch)
    {
        var tags = new List<string>(4);

        // Risk label
        tags.Add($"risk-{riskLevel}");

        // Feature category tag
        var featureCategoryTag = GetFeatureCategoryTag(group);
        tags.Add(featureCategoryTag);

        // Conversion category based on risk level
        var conversionCategory = riskLevel switch
        {
            1 or 2 => "automatic",
            3 => "semi-automatic",
            4 or 5 => "manual",
            _ => "manual"
        };
        tags.Add(conversionCategory);

        // Requires-research flag
        if (requiresResearch)
        {
            tags.Add("requires-research");
        }

        return tags;
    }

    private static string GetFeatureCategoryTag(StatementGroup group)
    {
        // Server-level features get the server-feature tag
        if (group.IsServerLevelFeature)
        {
            return "server-feature";
        }

        // Determine category from the first feature in the primary statement
        if (group.Statements.Count > 0)
        {
            var primaryStatement = group.Statements[0];
            if (primaryStatement.Features.Count > 0)
            {
                var category = primaryStatement.Features[0].Category;
                return category switch
                {
                    FeatureCategory.QueryFeature => "query-feature",
                    FeatureCategory.FunctionUsage => "function-usage",
                    FeatureCategory.TemporaryObject => "temporary-object",
                    FeatureCategory.TransactionFeature => "transaction-feature",
                    _ => "query-feature"
                };
            }
        }

        // Fallback for server-level or empty groups
        return "server-feature";
    }

    private static string? ValidateConfiguration(WorkItemConfiguration config)
    {
        if (config.MinimumRiskLevel < 1 || config.MinimumRiskLevel > 5)
        {
            return $"Configuration error: MinimumRiskLevel value '{config.MinimumRiskLevel}' is outside the valid range [1, 5].";
        }

        if (config.MaxWorkItemCount.HasValue && config.MaxWorkItemCount.Value < 1)
        {
            return $"Configuration error: MaxWorkItemCount value '{config.MaxWorkItemCount.Value}' is outside the valid range [1, ∞).";
        }

        return null;
    }

    private static WorkItemResult CreateFailedResult(string errorMessage)
    {
        return new WorkItemResult
        {
            WorkItems = [],
            Metadata = new WorkItemMetadata
            {
                GeneratedAt = DateTimeOffset.UtcNow,
                SourceAssessmentPath = null,
                TotalWorkItemCount = 0,
                TotalEstimatedEffort = new Models.HourRange { MinHours = 0, MaxHours = 0 }
            },
            Succeeded = false,
            ErrorMessage = errorMessage
        };
    }

    private static WorkItemResult CreateEmptyResult()
    {
        return new WorkItemResult
        {
            WorkItems = [],
            Metadata = new WorkItemMetadata
            {
                GeneratedAt = DateTimeOffset.UtcNow,
                SourceAssessmentPath = null,
                TotalWorkItemCount = 0,
                TotalEstimatedEffort = new Models.HourRange { MinHours = 0, MaxHours = 0 }
            },
            Succeeded = true
        };
    }

    /// <summary>
    /// Compares work items for descending priority ordering with tie-breaking.
    /// </summary>
    private static int CompareByPriorityDescending(WorkItem a, WorkItem b)
    {
        // Primary: PriorityScore descending
        var scoreComparison = b.PriorityScore.CompareTo(a.PriorityScore);
        if (scoreComparison != 0)
        {
            return scoreComparison;
        }

        // Tie-break 1: Risk level descending
        var riskComparison = b.RiskLevel.CompareTo(a.RiskLevel);
        if (riskComparison != 0)
        {
            return riskComparison;
        }

        // Tie-break 2: Statement count descending (via affected objects)
        var aStatementCount = a.AffectedObjects.Sum(ao => ao.StatementCount);
        var bStatementCount = b.AffectedObjects.Sum(ao => ao.StatementCount);
        return bStatementCount.CompareTo(aStatementCount);
    }
}
