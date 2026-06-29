using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using MigrationAssessment.WorkItems;
using MigrationAssessment.WorkItems.Models;

namespace MigrationAssessment.Cli;

/// <summary>
/// CLI command for generating work items from an assessment JSON file.
/// </summary>
public static class GenerateWorkItemsCommand
{
    public static async Task<int> RunAsync(string[] args, CancellationToken ct)
    {
        // The verb "generate-work-items" has already been stripped from args by the caller.
        var config = ParseWorkItemArgs(args);
        if (config is null)
        {
            PrintWorkItemUsage();
            return 1;
        }

        // Configure DI container with all work item generation services
        var services = new ServiceCollection();
        services.AddLogging(builder =>
        {
            builder.AddConsole();
            builder.SetMinimumLevel(LogLevel.Information);
        });
        services.AddSingleton<IStatementGrouper, StatementGrouper>();
        services.AddSingleton<IPriorityCalculator, PriorityCalculator>();
        services.AddSingleton<IEffortEstimator, EffortEstimator>();
        services.AddSingleton<IRemediationKnowledgeBase, RemediationKnowledgeBase>();
        services.AddSingleton<IPostgresConversionEngine, PostgresConversionEngine>();
        services.AddSingleton<WorkItemDeduplicator>();
        services.AddSingleton<TitleGenerator>();
        services.AddSingleton<DescriptionGenerator>();
        services.AddSingleton<RemediationGuidanceGenerator>();
        services.AddSingleton<AcceptanceCriteriaGenerator>();
        services.AddSingleton<AssessmentJsonReader>();
        services.AddSingleton<IWorkItemJsonWriter, WorkItemJsonWriter>();
        services.AddSingleton<IWorkItemMarkdownWriter, WorkItemMarkdownWriter>();
        services.AddSingleton<IWorkItemGenerator, WorkItemGeneratorService>();

        var serviceProvider = services.BuildServiceProvider();

        var generator = serviceProvider.GetRequiredService<IWorkItemGenerator>();

        // Invoke generator
        var result = await generator.GenerateFromFileAsync(
            config.InputFilePath,
            config.ToWorkItemConfiguration(),
            ct).ConfigureAwait(false);

        if (!result.Succeeded)
        {
            Console.Error.WriteLine($"Error: {result.ErrorMessage}");
            return 1;
        }

        Console.WriteLine($"Generated {result.Metadata.TotalWorkItemCount} work items.");
        Console.WriteLine($"JSON output: {config.OutputJsonPath}");

        if (config.MarkdownEnabled)
        {
            var mdPath = config.MarkdownOutputPath
                ?? ResolveDefaultMarkdownPath(config.OutputJsonPath);
            Console.WriteLine($"Markdown output: {mdPath}");
        }

        Console.WriteLine($"Estimated effort: {result.Metadata.TotalEstimatedEffort.MinHours:F1}-{result.Metadata.TotalEstimatedEffort.MaxHours:F1} hours");

        return 0;
    }

    private static WorkItemCommandConfig? ParseWorkItemArgs(string[] args)
    {
        if (args.Length == 0)
            return null;

        string? inputFilePath = null;
        string outputJsonPath = "./work-items.json";
        bool markdownEnabled = false;
        string? markdownOutputPath = null;
        int minRisk = 1;
        int? maxItems = null;

        int i = 0;

        // First non-option argument is the input file path
        if (args.Length > 0 && !args[0].StartsWith('-'))
        {
            inputFilePath = args[0];
            i = 1;
        }

        for (; i < args.Length; i++)
        {
            switch (args[i].ToLowerInvariant())
            {
                case "--output":
                    if (i + 1 < args.Length) outputJsonPath = args[++i];
                    break;
                case "--markdown":
                    markdownEnabled = true;
                    break;
                case "--markdown-output":
                    if (i + 1 < args.Length) markdownOutputPath = args[++i];
                    markdownEnabled = true; // Implicitly enable markdown if path is specified
                    break;
                case "--min-risk":
                    if (i + 1 < args.Length && int.TryParse(args[++i], out var risk))
                        minRisk = risk;
                    break;
                case "--max-items":
                    if (i + 1 < args.Length && int.TryParse(args[++i], out var max))
                        maxItems = max;
                    break;
                case "--help" or "-h":
                    return null;
                default:
                    // If we haven't captured the input file yet, treat as input
                    if (inputFilePath is null && !args[i].StartsWith('-'))
                    {
                        inputFilePath = args[i];
                    }
                    break;
            }
        }

        if (string.IsNullOrWhiteSpace(inputFilePath))
            return null;

        return new WorkItemCommandConfig
        {
            InputFilePath = inputFilePath,
            OutputJsonPath = outputJsonPath,
            MarkdownEnabled = markdownEnabled,
            MarkdownOutputPath = markdownOutputPath,
            MinRisk = minRisk,
            MaxItems = maxItems
        };
    }

    private static void PrintWorkItemUsage()
    {
        Console.WriteLine("Usage: MigrationAssessment generate-work-items <input-file-path> [options]");
        Console.WriteLine();
        Console.WriteLine("Arguments:");
        Console.WriteLine("  <input-file-path>       Path to the assessment JSON file (required)");
        Console.WriteLine();
        Console.WriteLine("Options:");
        Console.WriteLine("  --output <path>         Output JSON file path (default: ./work-items.json)");
        Console.WriteLine("  --markdown              Enable Markdown output generation");
        Console.WriteLine("  --markdown-output <path> Markdown output file path (default: same dir as JSON, work-items.md)");
        Console.WriteLine("  --min-risk <1-5>        Minimum risk level filter (default: 1)");
        Console.WriteLine("  --max-items <count>     Maximum number of work items to generate");
    }

    private static string ResolveDefaultMarkdownPath(string jsonOutputPath)
    {
        var directory = Path.GetDirectoryName(Path.GetFullPath(jsonOutputPath));
        if (string.IsNullOrEmpty(directory))
            directory = ".";
        return Path.Combine(directory, "work-items.md");
    }

    /// <summary>
    /// Internal configuration parsed from CLI arguments.
    /// </summary>
    private sealed class WorkItemCommandConfig
    {
        public required string InputFilePath { get; init; }
        public string OutputJsonPath { get; init; } = "./work-items.json";
        public bool MarkdownEnabled { get; init; }
        public string? MarkdownOutputPath { get; init; }
        public int MinRisk { get; init; } = 1;
        public int? MaxItems { get; init; }

        public WorkItemConfiguration ToWorkItemConfiguration() => new()
        {
            OutputJsonPath = OutputJsonPath,
            MarkdownEnabled = MarkdownEnabled,
            MarkdownOutputPath = MarkdownOutputPath,
            MinimumRiskLevel = MinRisk,
            MaxWorkItemCount = MaxItems
        };
    }
}
