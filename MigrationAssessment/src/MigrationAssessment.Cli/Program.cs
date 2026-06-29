using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using MigrationAssessment.Cli;
using MigrationAssessment.Core;
using MigrationAssessment.Core.Interfaces;
using MigrationAssessment.Core.Models;
using MigrationAssessment.Collectors;
using MigrationAssessment.Analysis;
using MigrationAssessment.Reporting;
using MigrationAssessment.WorkItems;

// Route to generate-work-items verb if specified
if (args.Length > 0 && args[0].Equals("generate-work-items", StringComparison.OrdinalIgnoreCase))
{
    using var workItemCts = new CancellationTokenSource();
    Console.CancelKeyPress += (_, e) => { e.Cancel = true; workItemCts.Cancel(); };
    return await GenerateWorkItemsCommand.RunAsync(args[1..], workItemCts.Token);
}

// Parse command-line arguments
var config = ParseArguments(args);
if (config is null)
{
    PrintUsage();
    return 1;
}

// Set up DI container
var services = new ServiceCollection();

// Logging
services.AddLogging(builder =>
{
    builder.AddConsole();
    builder.SetMinimumLevel(LogLevel.Information);
});

// Register services
services.AddTransient<IStatementCollector, QueryStoreCollector>();
services.AddTransient<IStatementCollector, ExtendedEventsCollector>();
services.AddTransient<IMetadataCollector, MetadataCollector>();
services.AddTransient<IFeatureDetector, FeatureDetector>();
services.AddTransient<IStatementParser, StatementParser>();
services.AddTransient<IStatementAnalyzer, StatementAnalyzer>();
services.AddTransient<IRiskScorer, RiskScorer>();
services.AddTransient<IWeightedComplexityCalculator, WeightedComplexityCalculator>();
services.AddTransient<IMigrationReadinessScorer, MigrationReadinessScorer>();
services.AddSingleton<IStatementObjectResolver, StatementObjectResolver>();
services.AddTransient<IObjectInventoryBuilder, ObjectInventoryBuilder>();
services.AddTransient<ISchemaAnalyzer, SchemaAnalyzer>();
services.AddTransient<IReportGenerator, ReportGenerator>();
services.AddTransient<IJsonReportWriter, JsonReportWriter>();

// Register work item generator (optional pipeline stage)
if (config.GenerateWorkItems)
{
    services.AddTransient<IStatementGrouper, StatementGrouper>();
    services.AddTransient<IPriorityCalculator, PriorityCalculator>();
    services.AddTransient<IEffortEstimator, EffortEstimator>();
    services.AddTransient<IRemediationKnowledgeBase, RemediationKnowledgeBase>();
    services.AddTransient<IPostgresConversionEngine, PostgresConversionEngine>();
    services.AddTransient<WorkItemDeduplicator>();
    services.AddTransient<TitleGenerator>();
    services.AddTransient<DescriptionGenerator>();
    services.AddTransient<RemediationGuidanceGenerator>();
    services.AddTransient<AcceptanceCriteriaGenerator>();
    services.AddTransient<AssessmentJsonReader>();
    services.AddTransient<IWorkItemJsonWriter, WorkItemJsonWriter>();
    services.AddTransient<IWorkItemMarkdownWriter, WorkItemMarkdownWriter>();
    services.AddTransient<IWorkItemGenerator, WorkItemGeneratorService>();
}

services.AddTransient<AssessmentPipeline>();

var serviceProvider = services.BuildServiceProvider();

// Run pipeline
var pipeline = serviceProvider.GetRequiredService<AssessmentPipeline>();
using var cts = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };

return await pipeline.RunAsync(config, cts.Token);

// --- Helper methods ---

static AssessmentConfiguration? ParseArguments(string[] args)
{
    if (args.Length == 0)
        return null;

    string? connectionString = null;
    string outputPath = "./assessment-output.json";
    double businessImportance = 1.0;
    bool generateWorkItems = false;
    string workItemOutputPath = "./work-items.json";
    bool workItemMarkdownEnabled = false;
    string? workItemMarkdownOutputPath = null;
    int workItemMinRiskLevel = 1;
    int? workItemMaxCount = null;

    for (int i = 0; i < args.Length; i++)
    {
        switch (args[i].ToLowerInvariant())
        {
            case "--connection-string" or "-c":
                if (i + 1 < args.Length) connectionString = args[++i];
                break;
            case "--output" or "-o":
                if (i + 1 < args.Length) outputPath = args[++i];
                break;
            case "--business-importance" or "-b":
                if (i + 1 < args.Length && double.TryParse(args[++i], out var bi))
                    businessImportance = bi;
                break;
            case "--generate-work-items":
                generateWorkItems = true;
                break;
            case "--work-item-output":
                if (i + 1 < args.Length) workItemOutputPath = args[++i];
                break;
            case "--work-item-markdown":
                workItemMarkdownEnabled = true;
                break;
            case "--work-item-markdown-output":
                if (i + 1 < args.Length) workItemMarkdownOutputPath = args[++i];
                break;
            case "--work-item-min-risk":
                if (i + 1 < args.Length && int.TryParse(args[++i], out var minRisk))
                    workItemMinRiskLevel = minRisk;
                break;
            case "--work-item-max-count":
                if (i + 1 < args.Length && int.TryParse(args[++i], out var maxCount))
                    workItemMaxCount = maxCount;
                break;
            case "--help" or "-h":
                return null;
            default:
                // If first arg doesn't start with --, treat it as connection string
                if (i == 0 && !args[i].StartsWith('-'))
                    connectionString = args[i];
                break;
        }
    }

    if (string.IsNullOrWhiteSpace(connectionString))
        return null;

    return new AssessmentConfiguration
    {
        ConnectionString = connectionString,
        OutputPath = outputPath,
        DefaultBusinessImportance = businessImportance,
        GenerateWorkItems = generateWorkItems,
        WorkItemOutputPath = workItemOutputPath,
        WorkItemMarkdownEnabled = workItemMarkdownEnabled,
        WorkItemMarkdownOutputPath = workItemMarkdownOutputPath,
        WorkItemMinRiskLevel = workItemMinRiskLevel,
        WorkItemMaxCount = workItemMaxCount
    };
}

static void PrintUsage()
{
    Console.WriteLine("Migration Assessment Engine");
    Console.WriteLine();
    Console.WriteLine("Usage: MigrationAssessment.Cli [options]");
    Console.WriteLine();
    Console.WriteLine("Options:");
    Console.WriteLine("  -c, --connection-string <string>     SQL Server connection string (required)");
    Console.WriteLine("  -o, --output <path>                  Output JSON file path (default: ./assessment-output.json)");
    Console.WriteLine("  -b, --business-importance <value>    Default business importance (1.0-5.0, default: 1.0)");
    Console.WriteLine("  -h, --help                           Show this help message");
    Console.WriteLine();
    Console.WriteLine("Work Item Generation:");
    Console.WriteLine("  --generate-work-items                Enable work item generation after assessment");
    Console.WriteLine("  --work-item-output <path>            Work items JSON output path (default: ./work-items.json)");
    Console.WriteLine("  --work-item-markdown                 Enable Markdown work items report");
    Console.WriteLine("  --work-item-markdown-output <path>   Markdown output path (default: same dir as JSON)");
    Console.WriteLine("  --work-item-min-risk <1-5>           Minimum risk level filter (default: 1)");
    Console.WriteLine("  --work-item-max-count <n>            Maximum number of work items to generate");
}
