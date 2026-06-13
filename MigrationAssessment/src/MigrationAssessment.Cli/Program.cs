using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using MigrationAssessment.Cli;
using MigrationAssessment.Core.Interfaces;
using MigrationAssessment.Core.Models;
using MigrationAssessment.Collectors;
using MigrationAssessment.Analysis;
using MigrationAssessment.Reporting;

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
services.AddTransient<IReportGenerator, ReportGenerator>();
services.AddTransient<IJsonReportWriter, JsonReportWriter>();
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
        DefaultBusinessImportance = businessImportance
    };
}

static void PrintUsage()
{
    Console.WriteLine("Migration Assessment Engine");
    Console.WriteLine();
    Console.WriteLine("Usage: MigrationAssessment.Cli [options]");
    Console.WriteLine();
    Console.WriteLine("Options:");
    Console.WriteLine("  -c, --connection-string <string>  SQL Server connection string (required)");
    Console.WriteLine("  -o, --output <path>               Output JSON file path (default: ./assessment-output.json)");
    Console.WriteLine("  -b, --business-importance <value>  Default business importance (1.0-5.0, default: 1.0)");
    Console.WriteLine("  -h, --help                         Show this help message");
}
