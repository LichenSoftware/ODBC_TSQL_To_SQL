using System.Data.Common;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging;
using MigrationAssessment.Core.Interfaces;
using MigrationAssessment.Core.Models;
using MigrationAssessment.WorkItems;
using MigrationAssessment.WorkItems.Models;

namespace MigrationAssessment.Cli;

/// <summary>
/// Orchestrates the full assessment pipeline: connection → collection → parsing → analysis → scoring → reporting.
/// </summary>
public sealed class AssessmentPipeline
{
    private readonly IEnumerable<IStatementCollector> _statementCollectors;
    private readonly IMetadataCollector _metadataCollector;
    private readonly IFeatureDetector _featureDetector;
    private readonly IStatementParser _parser;
    private readonly IStatementAnalyzer _analyzer;
    private readonly IRiskScorer _riskScorer;
    private readonly IWeightedComplexityCalculator _weightedCalculator;
    private readonly IReportGenerator _reportGenerator;
    private readonly IJsonReportWriter _jsonWriter;
    private readonly IObjectInventoryBuilder _objectInventoryBuilder;
    private readonly IWorkItemGenerator? _workItemGenerator;
    private readonly ILogger<AssessmentPipeline> _logger;

    public AssessmentPipeline(
        IEnumerable<IStatementCollector> statementCollectors,
        IMetadataCollector metadataCollector,
        IFeatureDetector featureDetector,
        IStatementParser parser,
        IStatementAnalyzer analyzer,
        IRiskScorer riskScorer,
        IWeightedComplexityCalculator weightedCalculator,
        IReportGenerator reportGenerator,
        IJsonReportWriter jsonWriter,
        IObjectInventoryBuilder objectInventoryBuilder,
        ILogger<AssessmentPipeline> logger,
        IWorkItemGenerator? workItemGenerator = null)
    {
        _statementCollectors = statementCollectors;
        _metadataCollector = metadataCollector;
        _featureDetector = featureDetector;
        _parser = parser;
        _analyzer = analyzer;
        _riskScorer = riskScorer;
        _weightedCalculator = weightedCalculator;
        _reportGenerator = reportGenerator;
        _jsonWriter = jsonWriter;
        _objectInventoryBuilder = objectInventoryBuilder;
        _logger = logger;
        _workItemGenerator = workItemGenerator;
    }

    /// <summary>
    /// Runs the full assessment pipeline and returns an exit code (0 = success, non-zero = failure).
    /// </summary>
    public async Task<int> RunAsync(AssessmentConfiguration config, CancellationToken ct)
    {
        // 1. Connect with retry logic (3 attempts, 5s delay, 30s timeout per Req 12.1)
        DbConnection connection;
        try
        {
            connection = await ConnectWithRetryAsync(config, ct);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "All connection retry attempts exhausted");
            return 1; // Non-zero exit per Req 12.2
        }

        using (connection)
        {
            var collectionOptions = new CollectionOptions
            {
                QueryTimeout = config.QueryTimeout,
                MaxBatchSize = 10_000
            };

            // 2. Run all collectors in parallel
            var failures = new List<CollectionFailure>();
            var allStatements = new List<CollectedStatement>();

            // Statement collectors (Query Store, Extended Events) run in parallel
            var collectorList = _statementCollectors.ToList();
            var collectorTasks = collectorList.Select(c =>
                CollectSafelyAsync(c, connection, collectionOptions, ct));
            var collectorResults = await Task.WhenAll(collectorTasks);

            for (int i = 0; i < collectorResults.Length; i++)
            {
                var result = collectorResults[i];
                var collector = collectorList[i];

                if (result.Succeeded)
                {
                    allStatements.AddRange(result.Statements);
                    _logger.LogInformation("Collector '{Source}' returned {Count} statements",
                        collector.SourceName, result.Statements.Count);
                }
                else
                {
                    failures.Add(new CollectionFailure
                    {
                        SourceName = collector.SourceName,
                        Reason = result.ErrorMessage ?? "Unknown error"
                    });
                    _logger.LogWarning("Collector '{Source}' failed: {Reason}",
                        collector.SourceName, result.ErrorMessage);
                }
            }

            // Metadata collector
            DatabaseObjectInventory objectInventory;
            try
            {
                objectInventory = await _metadataCollector.CollectAsync(connection, collectionOptions, ct);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Metadata collection failed");
                failures.Add(new CollectionFailure { SourceName = "Metadata", Reason = ex.Message });
                objectInventory = EmptyInventory();
            }

            // Feature detector
            FeatureDetectionResult featureResult;
            try
            {
                featureResult = await _featureDetector.DetectAsync(connection, collectionOptions, ct);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Feature detection failed");
                failures.Add(new CollectionFailure { SourceName = "Feature Detection", Reason = ex.Message });
                featureResult = EmptyFeatureResult();
            }

            // Req 12.5: If ALL sources fail, terminate with error — do not produce empty assessment
            var totalSources = collectorList.Count + 2; // +2 for metadata and feature detection
            if (failures.Count >= totalSources)
            {
                _logger.LogError("All data collection sources failed. Cannot produce assessment.");
                return 1;
            }

            // 3. Parse → Analyze → Score each collected statement
            var analyzedStatements = new List<AnalyzedStatement>();
            foreach (var statement in allStatements)
            {
                try
                {
                    var parsed = _parser.ParseBatch(statement.SqlText);
                    foreach (var parsedStmt in parsed)
                    {
                        var analysisResult = _analyzer.Analyze(parsedStmt, statement.QueryHash);
                        var riskScore = _riskScorer.ScoreStatement(analysisResult.Features, !parsedStmt.ParseSucceeded);
                        var weightedRisk = _weightedCalculator.CalculateWeightedRisk(
                            riskScore, statement.ExecutionCount, config.DefaultBusinessImportance);

                        analyzedStatements.Add(new AnalyzedStatement
                        {
                            Source = statement,
                            Classification = parsedStmt.Classification,
                            Features = analysisResult.Features,
                            RiskScore = riskScore,
                            WeightedRisk = weightedRisk,
                            ParseSucceeded = parsedStmt.ParseSucceeded,
                            ParseError = parsedStmt.ParseError,
                            ErrorLine = parsedStmt.ErrorLine,
                            ErrorColumn = parsedStmt.ErrorColumn,
                            AnalysisComplete = analysisResult.AnalysisComplete
                        });
                    }
                }
                catch (Exception ex)
                {
                    // Req 12.4: Log and continue — never terminate pipeline for a single statement
                    var truncatedText = statement.SqlText.Length > 1000
                        ? statement.SqlText[..1000] : statement.SqlText;
                    _logger.LogWarning(ex, "Unhandled exception analyzing statement: {Text}", truncatedText);

                    analyzedStatements.Add(new AnalyzedStatement
                    {
                        Source = statement,
                        Classification = StatementClassification.Unknown,
                        Features = Array.Empty<DetectedFeature>(),
                        RiskScore = 3,
                        WeightedRisk = 3 * statement.ExecutionCount * config.DefaultBusinessImportance,
                        ParseSucceeded = false,
                        ParseError = $"{ex.GetType().Name}: {ex.Message}",
                        AnalysisComplete = false
                    });
                }
            }

            // 4. Generate report
            var report = _reportGenerator.GenerateReport(analyzedStatements, objectInventory, featureResult, failures);

            // 4.5. Build parsed object inventory from analyzed statements + metadata
            var parsedObjectInventory = _objectInventoryBuilder.BuildInventory(analyzedStatements, objectInventory);

            // 5. Write JSON output
            var writeResult = await _jsonWriter.WriteAsync(
                report, analyzedStatements, objectInventory, featureResult, config.OutputPath, ct, parsedObjectInventory);

            if (!writeResult.Succeeded)
            {
                _logger.LogError("Failed to write report: {Error}", writeResult.ErrorMessage);
                return 1;
            }

            // 6. Generate work items (optional)
            if (config.GenerateWorkItems && _workItemGenerator is not null)
            {
                var workItemConfig = new WorkItemConfiguration
                {
                    OutputJsonPath = config.WorkItemOutputPath,
                    MarkdownEnabled = config.WorkItemMarkdownEnabled,
                    MarkdownOutputPath = config.WorkItemMarkdownOutputPath,
                    MinimumRiskLevel = config.WorkItemMinRiskLevel,
                    MaxWorkItemCount = config.WorkItemMaxCount
                };

                var workItemResult = _workItemGenerator.GenerateWorkItems(
                    analyzedStatements, featureResult, workItemConfig, parsedObjectInventory);

                if (workItemResult.Succeeded)
                {
                    _logger.LogInformation("Generated {Count} work items", workItemResult.Metadata.TotalWorkItemCount);
                }
                else
                {
                    _logger.LogWarning("Work item generation failed: {Error}", workItemResult.ErrorMessage);
                    // Don't fail the pipeline — work items are optional
                }
            }

            _logger.LogInformation("Assessment complete. Score: {Score}. Output: {Path}",
                report.Summary.MigrationReadinessScore, config.OutputPath);
            return 0;
        }
    }

    /// <summary>
    /// Connects to SQL Server with retry logic: 3 attempts, 5s delay, 30s timeout per attempt.
    /// </summary>
    private async Task<DbConnection> ConnectWithRetryAsync(AssessmentConfiguration config, CancellationToken ct)
    {
        for (int attempt = 1; attempt <= config.MaxRetryAttempts; attempt++)
        {
            try
            {
                var connectionString = new SqlConnectionStringBuilder(config.ConnectionString)
                {
                    ConnectTimeout = (int)config.ConnectionTimeout.TotalSeconds,
                    MultipleActiveResultSets = true
                }.ConnectionString;

                var connection = new SqlConnection(connectionString);
                await connection.OpenAsync(ct);
                _logger.LogInformation("Connected to SQL Server (attempt {Attempt})", attempt);
                return connection;
            }
            catch (Exception ex) when (attempt < config.MaxRetryAttempts)
            {
                _logger.LogWarning(ex, "Connection attempt {Attempt} failed. Retrying in {Delay}s...",
                    attempt, config.RetryDelay.TotalSeconds);
                await Task.Delay(config.RetryDelay, ct);
            }
        }

        // Final attempt — let the exception propagate
        var finalConnectionString = new SqlConnectionStringBuilder(config.ConnectionString)
        {
            ConnectTimeout = (int)config.ConnectionTimeout.TotalSeconds,
            MultipleActiveResultSets = true
        }.ConnectionString;

        var finalConnection = new SqlConnection(finalConnectionString);
        await finalConnection.OpenAsync(ct);
        _logger.LogInformation("Connected to SQL Server (attempt {Attempt})", config.MaxRetryAttempts);
        return finalConnection;
    }

    /// <summary>
    /// Runs a statement collector safely, catching exceptions and returning a failed result.
    /// </summary>
    private static async Task<CollectionResult> CollectSafelyAsync(
        IStatementCollector collector, DbConnection connection, CollectionOptions options, CancellationToken ct)
    {
        try
        {
            return await collector.CollectAsync(connection, options, ct);
        }
        catch (Exception ex)
        {
            return new CollectionResult
            {
                Statements = Array.Empty<CollectedStatement>(),
                Succeeded = false,
                ErrorMessage = $"{ex.GetType().Name}: {ex.Message}"
            };
        }
    }

    private static DatabaseObjectInventory EmptyInventory() => new()
    {
        Tables = Array.Empty<TableMetadata>(),
        Indexes = Array.Empty<IndexMetadata>(),
        Constraints = Array.Empty<ConstraintMetadata>(),
        ForeignKeys = Array.Empty<ForeignKeyMetadata>(),
        ProgrammableObjects = Array.Empty<ProgrammableObjectMetadata>(),
        Synonyms = Array.Empty<SynonymMetadata>()
    };

    private static FeatureDetectionResult EmptyFeatureResult() => new()
    {
        FeatureCounts = new Dictionary<string, int>(),
        DetailedInventory = Array.Empty<DetectedServerFeature>(),
        InaccessibleFeatures = Array.Empty<InaccessibleFeature>()
    };
}
