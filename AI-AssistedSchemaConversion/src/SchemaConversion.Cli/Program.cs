using System.CommandLine;
using System.CommandLine.Invocation;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Npgsql;
using SchemaConversion.AiEngine;
using SchemaConversion.Cli.Services;
using SchemaConversion.Core.Interfaces;
using SchemaConversion.Core.Models;
using SchemaConversion.Core.Options;
using SchemaConversion.Extraction;
using SchemaConversion.Orchestration;
using SchemaConversion.Reporting;
using SchemaConversion.RuleEngine;

namespace SchemaConversion.Cli;

public static class Program
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
    };

    public static async Task<int> Main(string[] args)
    {
        var rootCommand = new RootCommand("AI-Assisted Schema Conversion: converts SQL Server schemas to PostgreSQL");

        rootCommand.AddCommand(BuildExtractCommand());
        rootCommand.AddCommand(BuildConvertCommand());
        rootCommand.AddCommand(BuildRerunCommand());
        rootCommand.AddCommand(BuildReviewCommand());
        rootCommand.AddCommand(BuildEditCommand());
        rootCommand.AddCommand(BuildApproveCommand());
        rootCommand.AddCommand(BuildGenerateCommand());
        rootCommand.AddCommand(BuildGenerateMappingCommand());
        rootCommand.AddCommand(BuildReportCommand());
        rootCommand.AddCommand(BuildFixCommand());

        return await rootCommand.InvokeAsync(args);
    }

    private static ServiceProvider BuildServiceProvider(IConfiguration configuration)
    {
        var services = new ServiceCollection();

        // Logging
        services.AddLogging(builder =>
        {
            builder.AddConsole();
            builder.SetMinimumLevel(LogLevel.Information);
        });

        // Configuration sections
        var bedrockSection = configuration.GetSection("Bedrock");
        var conversionSection = configuration.GetSection("Conversion");
        var auditLogSection = configuration.GetSection("AuditLog");

        var bedrockOptions = new BedrockClientOptions
        {
            ModelId = bedrockSection["ModelId"] ?? "anthropic.claude-sonnet-4-20250514-v1:0",
            Timeout = TimeSpan.FromSeconds(bedrockSection.GetValue("Timeout", 120)),
            MaxRetryAttempts = bedrockSection.GetValue("MaxRetryAttempts", 3),
            Temperature = bedrockSection.GetValue("Temperature", 0.2),
            MaxOutputTokens = bedrockSection.GetValue("MaxOutputTokens", 8192)
        };

        var sessionDirectory = conversionSection["SessionDirectory"] ?? "./sessions";
        var typeMappingsFile = conversionSection["TypeMappingsFile"] ?? "./config/type-mappings.json";
        var functionMappingsFile = conversionSection["FunctionMappingsFile"] ?? "./config/function-mappings.json";
        var promptTemplatesDirectory = conversionSection["PromptTemplatesDirectory"] ?? "./config/prompts";
        var schemaMappingsFile = conversionSection["SchemaMappingsFile"] ?? "./config/schema-mappings.json";
        var auditDirectory = auditLogSection["Directory"] ?? "./sessions/{sessionId}/audit";
        var maxFileSizeBytes = auditLogSection.GetValue("MaxFileSizeBytes", 52428800L);

        // Register BedrockClientOptions
        services.AddSingleton(bedrockOptions);

        // Extraction
        services.AddSingleton<DdlFileSchemaExtractor>();
        services.AddSingleton<SqlServerSchemaExtractor>();
        services.AddSingleton<DependencyGraphBuilder>();

        // Rule Engine (requires config file paths)
        services.AddSingleton(sp => new TypeMapper(
            typeMappingsFile,
            sp.GetRequiredService<ILogger<TypeMapper>>()));
        services.AddSingleton(sp => new FunctionMapper(
            functionMappingsFile,
            sp.GetRequiredService<ILogger<FunctionMapper>>()));
        services.AddSingleton<ExpressionTranslator>();
        services.AddSingleton<TableConverter>();
        services.AddSingleton<ConstraintConverter>();
        services.AddSingleton<IndexConverter>();
        services.AddSingleton<SequenceConverter>();
        services.AddSingleton<ViewConverter>();
        services.AddSingleton<RuleEngine.SchemaConverter>();
        services.AddSingleton<UserDefinedTypeConverter>();
        services.AddSingleton<SynonymConverter>();
        services.AddSingleton<PermissionConverter>();
        services.AddSingleton<RuleBasedConverterRouter>();
        services.AddSingleton<IRuleBasedConverter>(sp => sp.GetRequiredService<RuleBasedConverterRouter>());

        // AI Engine
        services.AddSingleton<BedrockClient>();
        services.AddSingleton(sp => new PromptManager(
            promptTemplatesDirectory,
            sp.GetRequiredService<ILogger<PromptManager>>()));
        services.AddSingleton<AiResponseParser>();
        services.AddSingleton<AiConverterService>();
        services.AddSingleton<IAiConverter>(sp => sp.GetRequiredService<AiConverterService>());

        // Extraction (register default ISchemaExtractor as DdlFileSchemaExtractor)
        services.AddSingleton<ISchemaExtractor>(sp => sp.GetRequiredService<DdlFileSchemaExtractor>());

        // Orchestration
        services.AddSingleton(sp => new SchemaMappingLoader(
            schemaMappingsFile,
            sp.GetRequiredService<ILogger<SchemaMappingLoader>>()));
        services.AddSingleton(sp => new ConversionSessionStore(
            sessionDirectory,
            sp.GetRequiredService<ILogger<ConversionSessionStore>>()));
        services.AddSingleton<IConversionSessionStore>(sp => sp.GetRequiredService<ConversionSessionStore>());
        services.AddSingleton(sp => new AuditLogWriter(
            auditDirectory,
            sp.GetRequiredService<ILogger<AuditLogWriter>>(),
            maxFileSizeBytes));
        services.AddSingleton<IAuditLogWriter>(sp => sp.GetRequiredService<AuditLogWriter>());
        services.AddSingleton<SessionChangeDetector>();
        services.AddSingleton<IObjectClassifier>(sp => new ObjectClassifier(
            sp.GetRequiredService<ILogger<ObjectClassifier>>()));
        services.AddSingleton<ConversionPipeline>();

        // Reporting
        services.AddSingleton<ConversionReportGenerator>();
        services.AddSingleton<IConversionReportGenerator>(sp => sp.GetRequiredService<ConversionReportGenerator>());
        services.AddSingleton<ProcedureMappingGenerator>();
        services.AddSingleton<ScriptOrderResolver>();
        services.AddSingleton<ScriptGenerator>();
        services.AddSingleton<IScriptGenerator>(sp => sp.GetRequiredService<ScriptGenerator>());

        return services.BuildServiceProvider();
    }

    private static IConfiguration LoadConfiguration()
    {
        return new ConfigurationBuilder()
            .SetBasePath(Directory.GetCurrentDirectory())
            .AddJsonFile("appsettings.json", optional: false, reloadOnChange: false)
            .Build();
    }

    private static void ValidateConfiguration(IConfiguration configuration, ILogger logger)
    {
        var conversionSection = configuration.GetSection("Conversion");
        var typeMappingsFile = conversionSection["TypeMappingsFile"] ?? "./config/type-mappings.json";
        var functionMappingsFile = conversionSection["FunctionMappingsFile"] ?? "./config/function-mappings.json";
        var promptTemplatesDirectory = conversionSection["PromptTemplatesDirectory"] ?? "./config/prompts";

        if (!File.Exists(typeMappingsFile))
        {
            logger.LogWarning("Type mappings file not found: {Path}", typeMappingsFile);
        }

        if (!File.Exists(functionMappingsFile))
        {
            logger.LogWarning("Function mappings file not found: {Path}", functionMappingsFile);
        }

        if (!Directory.Exists(promptTemplatesDirectory))
        {
            logger.LogWarning("Prompt templates directory not found: {Path}", promptTemplatesDirectory);
        }
        else
        {
            var templates = Directory.GetFiles(promptTemplatesDirectory, "*.md");
            if (templates.Length == 0)
            {
                logger.LogWarning("No prompt templates found in: {Path}", promptTemplatesDirectory);
            }
        }
    }

    // ─── Extract Command ───────────────────────────────────────────────

    private static Command BuildExtractCommand()
    {
        var connectionOption = new Option<string?>("--connection", "SQL Server connection string");
        var filesOption = new Option<string?>("--files", "Path to DDL files directory");
        var outputOption = new Option<string>("--output", "Output session directory") { IsRequired = true };

        var command = new Command("extract", "Extract schema objects from SQL Server or DDL files")
        {
            connectionOption,
            filesOption,
            outputOption
        };

        command.SetHandler(async (InvocationContext context) =>
        {
            var connection = context.ParseResult.GetValueForOption(connectionOption);
            var files = context.ParseResult.GetValueForOption(filesOption);
            var output = context.ParseResult.GetValueForOption(outputOption)!;

            if (string.IsNullOrWhiteSpace(connection) && string.IsNullOrWhiteSpace(files))
            {
                Console.Error.WriteLine("Error: Either --connection or --files must be specified.");
                context.ExitCode = 1;
                return;
            }

            var configuration = LoadConfiguration();
            using var serviceProvider = BuildServiceProvider(configuration);
            var logger = serviceProvider.GetRequiredService<ILogger<ConversionPipeline>>();
            ValidateConfiguration(configuration, logger);

            var sessionStore = serviceProvider.GetRequiredService<ConversionSessionStore>();
            var ct = context.GetCancellationToken();

            ISchemaExtractor extractor = !string.IsNullOrWhiteSpace(connection)
                ? serviceProvider.GetRequiredService<SqlServerSchemaExtractor>()
                : serviceProvider.GetRequiredService<DdlFileSchemaExtractor>();

            var extractionOptions = new SchemaExtractionOptions
            {
                ConnectionString = connection,
                FilePaths = !string.IsNullOrWhiteSpace(files)
                    ? Directory.GetFiles(files, "*.sql", SearchOption.AllDirectories).ToList()
                    : null
            };

            Console.WriteLine("Extracting schema objects...");
            var objects = await extractor.ExtractAsync(extractionOptions, ct);
            Console.WriteLine($"Extracted {objects.Count} objects.");

            // Create session
            var sessionId = Path.GetFileName(output);
            var session = await sessionStore.LoadOrCreateAsync(sessionId, ct);

            // Persist each extracted object as a Pending entry in the session
            Console.WriteLine("Saving objects to session...");
            foreach (var obj in objects)
            {
                var entry = new ConversionSessionEntry
                {
                    Source = obj,
                    Result = new ConversionResult
                    {
                        ObjectName = obj.Name,
                        SchemaName = obj.SchemaName,
                        ObjectType = obj.ObjectType,
                        Status = ConversionStatus.Pending,
                        Method = ConversionMethod.RuleBased
                    },
                    ConvertedAt = DateTimeOffset.UtcNow,
                    IsManuallyEdited = false
                };
                await sessionStore.SaveEntryAsync(sessionId, entry, ct);
            }

            Console.WriteLine($"Session created: {sessionId} at {output}");
            Console.WriteLine($"Objects discovered: {objects.Count}");

            context.ExitCode = 0;
        });

        return command;
    }

    // ─── Convert Command ───────────────────────────────────────────────

    private static Command BuildConvertCommand()
    {
        var sessionOption = new Option<string>("--session", "Session directory path") { IsRequired = true };
        var schemaOption = new Option<string?>("--schema", "Filter by schema name");
        var typeOption = new Option<string?>("--type", "Filter by object type");
        var objectsOption = new Option<string[]?>("--objects", "Filter by specific object names");
        var forceAiOption = new Option<string[]?>("--force-ai", "Force AI conversion for these objects");
        var forceRulesOption = new Option<string[]?>("--force-rules", "Force rule-based conversion for these objects");
        var concurrencyOption = new Option<int>("--concurrency", () => 4, "Maximum parallel conversions");

        var command = new Command("convert", "Convert schema objects from SQL Server to PostgreSQL")
        {
            sessionOption,
            schemaOption,
            typeOption,
            objectsOption,
            forceAiOption,
            forceRulesOption,
            concurrencyOption
        };

        command.SetHandler(async (InvocationContext context) =>
        {
            var session = context.ParseResult.GetValueForOption(sessionOption)!;
            var schema = context.ParseResult.GetValueForOption(schemaOption);
            var type = context.ParseResult.GetValueForOption(typeOption);
            var objects = context.ParseResult.GetValueForOption(objectsOption);
            var forceAi = context.ParseResult.GetValueForOption(forceAiOption);
            var forceRules = context.ParseResult.GetValueForOption(forceRulesOption);
            var concurrency = context.ParseResult.GetValueForOption(concurrencyOption);

            var configuration = LoadConfiguration();
            using var serviceProvider = BuildServiceProvider(configuration);
            var logger = serviceProvider.GetRequiredService<ILogger<ConversionPipeline>>();
            ValidateConfiguration(configuration, logger);

            var ct = context.GetCancellationToken();

            // Build filters
            SchemaObjectType? parsedType = null;
            if (!string.IsNullOrWhiteSpace(type) && Enum.TryParse<SchemaObjectType>(type, ignoreCase: true, out var t))
            {
                parsedType = t;
            }

            var filters = new ConversionFilters
            {
                Schemas = !string.IsNullOrWhiteSpace(schema) ? [schema] : null,
                Types = parsedType.HasValue ? [parsedType.Value] : null,
                Objects = objects?.Length > 0 ? objects.ToList() : null
            };

            var pipelineOptions = new ConversionPipelineOptions
            {
                SessionId = Path.GetFileName(session),
                Extraction = new SchemaExtractionOptions(),
                Filters = filters,
                Concurrency = concurrency,
                ForceAiObjects = forceAi?.ToList(),
                ForceRulesObjects = forceRules?.ToList()
            };

            Console.WriteLine($"Starting conversion for session: {pipelineOptions.SessionId}");
            Console.WriteLine($"Concurrency: {concurrency}");

            var pipeline = serviceProvider.GetRequiredService<ConversionPipeline>();
            var result = await pipeline.ExecuteAsync(pipelineOptions, ct);

            Console.WriteLine();
            Console.WriteLine("Conversion complete:");
            Console.WriteLine($"  Total processed: {result.TotalProcessed}");
            Console.WriteLine($"  Converted: {result.Converted}");
            Console.WriteLine($"  Flagged: {result.Flagged}");
            Console.WriteLine($"  Failed: {result.Failed}");
            Console.WriteLine($"  Duration: {result.Duration}");

            context.ExitCode = 0;
        });

        return command;
    }

    // ─── Rerun Command ─────────────────────────────────────────────────

    private static Command BuildRerunCommand()
    {
        var sessionOption = new Option<string>("--session", "Session directory path") { IsRequired = true };
        var objectsOption = new Option<string[]>("--objects", "Object names to re-convert") { IsRequired = true };

        var command = new Command("rerun", "Re-convert specified objects in a session")
        {
            sessionOption,
            objectsOption
        };

        command.SetHandler(async (InvocationContext context) =>
        {
            var session = context.ParseResult.GetValueForOption(sessionOption)!;
            var objects = context.ParseResult.GetValueForOption(objectsOption)!;

            var configuration = LoadConfiguration();
            using var serviceProvider = BuildServiceProvider(configuration);
            var logger = serviceProvider.GetRequiredService<ILogger<ConversionPipeline>>();
            ValidateConfiguration(configuration, logger);

            var ct = context.GetCancellationToken();

            var pipelineOptions = new ConversionPipelineOptions
            {
                SessionId = Path.GetFileName(session),
                Extraction = new SchemaExtractionOptions(),
                Filters = new ConversionFilters
                {
                    Objects = objects.ToList()
                }
            };

            Console.WriteLine($"Re-running conversion for {objects.Length} object(s) in session: {pipelineOptions.SessionId}");

            var pipeline = serviceProvider.GetRequiredService<ConversionPipeline>();
            var result = await pipeline.ExecuteAsync(pipelineOptions, ct);

            Console.WriteLine();
            Console.WriteLine("Re-conversion complete:");
            Console.WriteLine($"  Total processed: {result.TotalProcessed}");
            Console.WriteLine($"  Converted: {result.Converted}");
            Console.WriteLine($"  Flagged: {result.Flagged}");
            Console.WriteLine($"  Failed: {result.Failed}");

            context.ExitCode = 0;
        });

        return command;
    }

    // ─── Review Command ────────────────────────────────────────────────

    private static Command BuildReviewCommand()
    {
        var sessionOption = new Option<string>("--session", "Session directory path") { IsRequired = true };
        var flaggedOnlyOption = new Option<bool>("--flagged-only", () => false, "Show only flagged objects");

        var command = new Command("review", "Display conversion results for review")
        {
            sessionOption,
            flaggedOnlyOption
        };

        command.SetHandler(async (InvocationContext context) =>
        {
            var session = context.ParseResult.GetValueForOption(sessionOption)!;
            var flaggedOnly = context.ParseResult.GetValueForOption(flaggedOnlyOption);

            var configuration = LoadConfiguration();
            using var serviceProvider = BuildServiceProvider(configuration);
            var logger = serviceProvider.GetRequiredService<ILogger<ConversionPipeline>>();
            ValidateConfiguration(configuration, logger);

            var ct = context.GetCancellationToken();
            var sessionStore = serviceProvider.GetRequiredService<ConversionSessionStore>();
            var sessionId = Path.GetFileName(session);

            var entries = await sessionStore.GetAllEntriesAsync(sessionId, ct);

            if (flaggedOnly)
            {
                entries = entries.Where(e =>
                    e.Result.Status == ConversionStatus.Flagged ||
                    e.Result.ReviewFlags.Count > 0).ToList();
            }

            Console.WriteLine($"Session: {sessionId}");
            Console.WriteLine($"Objects: {entries.Count}");
            Console.WriteLine(new string('─', 80));

            foreach (var entry in entries)
            {
                Console.WriteLine($"  {entry.Source.SchemaName}.{entry.Source.Name} ({entry.Source.ObjectType})");
                Console.WriteLine($"    Status: {entry.Result.Status} | Method: {entry.Result.Method}");

                if (entry.Result.ConfidenceScore.HasValue)
                {
                    Console.WriteLine($"    Confidence: {entry.Result.ConfidenceScore:P0}");
                }

                if (entry.Result.ReviewFlags.Count > 0)
                {
                    Console.WriteLine("    Review flags:");
                    foreach (var flag in entry.Result.ReviewFlags)
                    {
                        Console.WriteLine($"      ⚠ {flag.Reason}");
                    }
                }

                if (entry.Result.Assumptions.Count > 0)
                {
                    Console.WriteLine("    Assumptions:");
                    foreach (var assumption in entry.Result.Assumptions)
                    {
                        Console.WriteLine($"      • {assumption}");
                    }
                }

                if (entry.IsManuallyEdited)
                {
                    Console.WriteLine("    [Manually edited]");
                }

                Console.WriteLine();
            }

            context.ExitCode = 0;
        });

        return command;
    }

    // ─── Edit Command ──────────────────────────────────────────────────

    private static Command BuildEditCommand()
    {
        var sessionOption = new Option<string>("--session", "Session directory path") { IsRequired = true };
        var objectOption = new Option<string>("--object", "Object name (schema.name)") { IsRequired = true };
        var fileOption = new Option<string>("--file", "Path to edited DDL file") { IsRequired = true };

        var command = new Command("edit", "Persist a manual edit to a converted object")
        {
            sessionOption,
            objectOption,
            fileOption
        };

        command.SetHandler(async (InvocationContext context) =>
        {
            var session = context.ParseResult.GetValueForOption(sessionOption)!;
            var objectName = context.ParseResult.GetValueForOption(objectOption)!;
            var file = context.ParseResult.GetValueForOption(fileOption)!;

            if (!File.Exists(file))
            {
                Console.Error.WriteLine($"Error: File not found: {file}");
                context.ExitCode = 1;
                return;
            }

            var configuration = LoadConfiguration();
            using var serviceProvider = BuildServiceProvider(configuration);
            var logger = serviceProvider.GetRequiredService<ILogger<ConversionPipeline>>();
            ValidateConfiguration(configuration, logger);

            var ct = context.GetCancellationToken();
            var sessionStore = serviceProvider.GetRequiredService<ConversionSessionStore>();
            var sessionId = Path.GetFileName(session);

            // Parse object name (schema.name format)
            var parts = objectName.Split('.', 2);
            var schemaName = parts.Length > 1 ? parts[0] : "dbo";
            var name = parts.Length > 1 ? parts[1] : parts[0];

            var existingEntry = await sessionStore.GetEntryAsync(sessionId, schemaName, name, ct);

            if (existingEntry is null)
            {
                Console.Error.WriteLine($"Error: Object '{objectName}' not found in session.");
                context.ExitCode = 1;
                return;
            }

            var editedDdl = await File.ReadAllTextAsync(file, ct);

            var updatedEntry = existingEntry with
            {
                Result = existingEntry.Result with
                {
                    GeneratedDdl = editedDdl,
                    Status = ConversionStatus.ManuallyReviewed,
                    Method = ConversionMethod.Manual
                },
                IsManuallyEdited = true,
                ConvertedAt = DateTimeOffset.UtcNow
            };

            await sessionStore.SaveEntryAsync(sessionId, updatedEntry, ct);
            Console.WriteLine($"Edit saved for {objectName}.");

            context.ExitCode = 0;
        });

        return command;
    }

    // ─── Approve Command ───────────────────────────────────────────────

    private static Command BuildApproveCommand()
    {
        var sessionOption = new Option<string>("--session", "Session directory path") { IsRequired = true };
        var objectsOption = new Option<string[]?>("--objects", "Object names to approve");
        var allOption = new Option<bool>("--all", () => false, "Approve all objects");

        var command = new Command("approve", "Mark objects as approved")
        {
            sessionOption,
            objectsOption,
            allOption
        };

        command.SetHandler(async (InvocationContext context) =>
        {
            var session = context.ParseResult.GetValueForOption(sessionOption)!;
            var objects = context.ParseResult.GetValueForOption(objectsOption);
            var all = context.ParseResult.GetValueForOption(allOption);

            if (!all && (objects is null || objects.Length == 0))
            {
                Console.Error.WriteLine("Error: Either --objects or --all must be specified.");
                context.ExitCode = 1;
                return;
            }

            var configuration = LoadConfiguration();
            using var serviceProvider = BuildServiceProvider(configuration);
            var logger = serviceProvider.GetRequiredService<ILogger<ConversionPipeline>>();
            ValidateConfiguration(configuration, logger);

            var ct = context.GetCancellationToken();
            var sessionStore = serviceProvider.GetRequiredService<ConversionSessionStore>();
            var sessionId = Path.GetFileName(session);

            var entries = await sessionStore.GetAllEntriesAsync(sessionId, ct);
            var approvedCount = 0;

            foreach (var entry in entries)
            {
                var qualifiedName = $"{entry.Source.SchemaName}.{entry.Source.Name}";

                if (!all && objects is not null && !objects.Any(o =>
                    string.Equals(o, qualifiedName, StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(o, entry.Source.Name, StringComparison.OrdinalIgnoreCase)))
                {
                    continue;
                }

                if (entry.Result.Status == ConversionStatus.ManuallyReviewed)
                {
                    continue; // Already approved/reviewed
                }

                var updatedEntry = entry with
                {
                    Result = entry.Result with
                    {
                        Status = ConversionStatus.ManuallyReviewed
                    }
                };

                await sessionStore.SaveEntryAsync(sessionId, updatedEntry, ct);
                approvedCount++;
            }

            Console.WriteLine($"Approved {approvedCount} object(s) in session {sessionId}.");
            context.ExitCode = 0;
        });

        return command;
    }

    // ─── Generate Command ──────────────────────────────────────────────

    private static Command BuildGenerateCommand()
    {
        var sessionOption = new Option<string>("--session", "Session directory path") { IsRequired = true };
        var outputOption = new Option<string>("--output", "Output directory for DDL scripts") { IsRequired = true };
        var modeOption = new Option<string>("--mode", () => "consolidated",
            "Output mode: consolidated, per-schema, per-type, per-object");

        var command = new Command("generate", "Produce DDL scripts from conversion results")
        {
            sessionOption,
            outputOption,
            modeOption
        };

        command.SetHandler(async (InvocationContext context) =>
        {
            var session = context.ParseResult.GetValueForOption(sessionOption)!;
            var output = context.ParseResult.GetValueForOption(outputOption)!;
            var mode = context.ParseResult.GetValueForOption(modeOption)!;

            var configuration = LoadConfiguration();
            using var serviceProvider = BuildServiceProvider(configuration);
            var logger = serviceProvider.GetRequiredService<ILogger<ConversionPipeline>>();
            ValidateConfiguration(configuration, logger);

            var ct = context.GetCancellationToken();
            var sessionStore = serviceProvider.GetRequiredService<ConversionSessionStore>();
            var scriptGenerator = serviceProvider.GetRequiredService<ScriptGenerator>();
            var sessionId = Path.GetFileName(session);

            var entries = await sessionStore.GetAllEntriesAsync(sessionId, ct);

            var outputMode = mode.ToLowerInvariant() switch
            {
                "per-schema" or "perschema" => ScriptOutputMode.PerSchema,
                "per-type" or "pertype" => ScriptOutputMode.PerType,
                "per-object" or "perobject" => ScriptOutputMode.PerObject,
                _ => ScriptOutputMode.Consolidated
            };

            var options = new ScriptGenerationOptions
            {
                OutputDirectory = output,
                Mode = outputMode,
                IncludeComments = true
            };

            Console.WriteLine($"Generating DDL scripts (mode: {outputMode})...");
            await scriptGenerator.GenerateAsync(entries, options, ct);
            Console.WriteLine($"Scripts written to: {output}");

            context.ExitCode = 0;
        });

        return command;
    }

    // ─── Generate Mapping Command ─────────────────────────────────────

    private static Command BuildGenerateMappingCommand()
    {
        var sessionOption = new Option<string>("--session", "Session directory path") { IsRequired = true };
        var outputOption = new Option<string>("--output", "Output path for the mapping JSON file") { IsRequired = true };

        var command = new Command("generate-mapping",
            "Generate a PgPassthrough procedure mapping manifest from conversion results")
        {
            sessionOption,
            outputOption
        };

        command.SetHandler(async (InvocationContext context) =>
        {
            var session = context.ParseResult.GetValueForOption(sessionOption)!;
            var output = context.ParseResult.GetValueForOption(outputOption)!;

            var configuration = LoadConfiguration();
            using var serviceProvider = BuildServiceProvider(configuration);
            var logger = serviceProvider.GetRequiredService<ILogger<ConversionPipeline>>();
            ValidateConfiguration(configuration, logger);

            var ct = context.GetCancellationToken();
            var sessionStore = serviceProvider.GetRequiredService<ConversionSessionStore>();
            var mappingGenerator = serviceProvider.GetRequiredService<ProcedureMappingGenerator>();
            var sessionId = Path.GetFileName(session);

            var entries = await sessionStore.GetAllEntriesAsync(sessionId, ct);

            Console.WriteLine($"Generating procedure mapping manifest for session: {sessionId}...");
            var manifest = mappingGenerator.Generate(sessionId, entries);

            var json = JsonSerializer.Serialize(manifest, JsonOptions);
            var outputDir = Path.GetDirectoryName(output);
            if (!string.IsNullOrEmpty(outputDir))
            {
                Directory.CreateDirectory(outputDir);
            }
            await File.WriteAllTextAsync(output, json, ct);

            Console.WriteLine($"Mapping manifest written to: {output}");
            Console.WriteLine($"  Total mappings: {manifest.Summary.TotalMappings}");
            Console.WriteLine($"  Converted: {manifest.Summary.Converted}");
            Console.WriteLine($"  Flagged: {manifest.Summary.Flagged}");
            Console.WriteLine($"  Failed: {manifest.Summary.Failed}");
            Console.WriteLine($"  With parameters: {manifest.Summary.WithParameters}");
            Console.WriteLine($"  No parameters: {manifest.Summary.NoParameters}");

            context.ExitCode = 0;
        });

        return command;
    }

    // ─── Report Command ────────────────────────────────────────────────

    private static Command BuildReportCommand()
    {
        var sessionOption = new Option<string>("--session", "Session directory path") { IsRequired = true };
        var outputOption = new Option<string>("--output", "Output path for JSON report") { IsRequired = true };

        var command = new Command("report", "Produce a JSON conversion report")
        {
            sessionOption,
            outputOption
        };

        command.SetHandler(async (InvocationContext context) =>
        {
            var session = context.ParseResult.GetValueForOption(sessionOption)!;
            var output = context.ParseResult.GetValueForOption(outputOption)!;

            var configuration = LoadConfiguration();
            using var serviceProvider = BuildServiceProvider(configuration);
            var logger = serviceProvider.GetRequiredService<ILogger<ConversionPipeline>>();
            ValidateConfiguration(configuration, logger);

            var ct = context.GetCancellationToken();
            var sessionStore = serviceProvider.GetRequiredService<ConversionSessionStore>();
            var reportGenerator = serviceProvider.GetRequiredService<ConversionReportGenerator>();
            var sessionId = Path.GetFileName(session);

            var entries = await sessionStore.GetAllEntriesAsync(sessionId, ct);

            Console.WriteLine($"Generating report for session: {sessionId} ({entries.Count} objects)...");
            var report = await reportGenerator.GenerateAsync(sessionId, entries, ct);

            var json = JsonSerializer.Serialize(report, JsonOptions);
            var outputDir = Path.GetDirectoryName(output);
            if (!string.IsNullOrEmpty(outputDir))
            {
                Directory.CreateDirectory(outputDir);
            }
            await File.WriteAllTextAsync(output, json, ct);
            Console.WriteLine($"Report written to: {output}");

            context.ExitCode = 0;
        });

        return command;
    }

    // ─── Fix Command ───────────────────────────────────────────────────

    private static Command BuildFixCommand()
    {
        var failedDdlOption = new Option<string>("--failed-ddl", "The failed PostgreSQL DDL statement") { IsRequired = true };
        var errorOption = new Option<string>("--error", "The PostgreSQL error message") { IsRequired = true };
        var sourceTsqlOption = new Option<string?>("--source-tsql", "The original T-SQL source definition for context");
        var pgConnectionOption = new Option<string>("--pg-connection", "PostgreSQL connection string for applying fixes") { IsRequired = true };
        var maxAttemptsOption = new Option<int>("--max-attempts", () => 2, "Maximum number of fix attempts");

        var command = new Command("fix", "Attempt AI-assisted fix of failed PostgreSQL DDL via Bedrock")
        {
            failedDdlOption,
            errorOption,
            sourceTsqlOption,
            pgConnectionOption,
            maxAttemptsOption
        };

        command.SetHandler(async (InvocationContext context) =>
        {
            var failedDdl = context.ParseResult.GetValueForOption(failedDdlOption)!;
            var error = context.ParseResult.GetValueForOption(errorOption)!;
            var sourceTsql = context.ParseResult.GetValueForOption(sourceTsqlOption);
            var pgConnection = context.ParseResult.GetValueForOption(pgConnectionOption)!;
            var maxAttempts = context.ParseResult.GetValueForOption(maxAttemptsOption);

            var configuration = LoadConfiguration();
            using var serviceProvider = BuildFixServiceProvider(configuration);
            var fixService = serviceProvider.GetRequiredService<BedrockFixService>();
            var logger = serviceProvider.GetRequiredService<ILogger<BedrockFixService>>();
            var ct = context.GetCancellationToken();

            var currentDdl = failedDdl;
            var currentError = error;
            var errors = new List<string> { error };
            var attempts = 0;
            var success = false;
            string? fixedDdl = null;
            string? explanation = null;

            for (var attempt = 1; attempt <= maxAttempts; attempt++)
            {
                attempts = attempt;
                logger.LogInformation("Fix attempt {Attempt}/{MaxAttempts}", attempt, maxAttempts);

                // Request fix from Bedrock
                var fixResult = await fixService.RequestFixAsync(currentDdl, currentError, sourceTsql);

                if (!fixResult.Success)
                {
                    // AI service itself failed
                    errors.Add(fixResult.ErrorMessage ?? "AI fix request failed");
                    break;
                }

                fixedDdl = fixResult.FixedDdl;
                explanation = fixResult.Explanation;

                // Try applying the fix to PostgreSQL
                var applyError = await TryApplyDdlAsync(fixedDdl, pgConnection, ct);

                if (applyError is null)
                {
                    // Success - DDL applied without error
                    success = true;
                    logger.LogInformation("Fix succeeded on attempt {Attempt}", attempt);
                    break;
                }

                // DDL still fails - prepare for next attempt
                logger.LogWarning("Fix attempt {Attempt} failed: {Error}", attempt, applyError);
                errors.Add(applyError);
                currentDdl = fixedDdl;
                currentError = applyError;
            }

            // Output JSON result to stdout
            var result = new
            {
                success,
                fixedDdl = fixedDdl ?? currentDdl,
                attempts,
                explanation = explanation ?? "",
                errors
            };

            var json = JsonSerializer.Serialize(result, JsonOptions);
            Console.WriteLine(json);

            context.ExitCode = success ? 0 : 1;
        });

        return command;
    }

    /// <summary>
    /// Attempts to apply DDL to PostgreSQL. Returns null on success, or the error message on failure.
    /// </summary>
    private static async Task<string?> TryApplyDdlAsync(string ddl, string connectionString, CancellationToken ct)
    {
        try
        {
            await using var connection = new NpgsqlConnection(connectionString);
            await connection.OpenAsync(ct);
            await using var cmd = new NpgsqlCommand(ddl, connection);
            await cmd.ExecuteNonQueryAsync(ct);
            return null;
        }
        catch (PostgresException ex)
        {
            return $"{ex.MessageText} (Position: {ex.Position}, SQLSTATE: {ex.SqlState})";
        }
        catch (NpgsqlException ex)
        {
            return $"Connection error: {ex.Message}";
        }
    }

    /// <summary>
    /// Builds a lightweight service provider for the fix command with only BedrockFixService and Npgsql dependencies.
    /// </summary>
    private static ServiceProvider BuildFixServiceProvider(IConfiguration configuration)
    {
        var services = new ServiceCollection();

        services.AddLogging(builder =>
        {
            builder.AddConsole();
            builder.SetMinimumLevel(LogLevel.Information);
        });

        services.AddSingleton<IConfiguration>(configuration);
        services.AddSingleton<BedrockFixService>();

        return services.BuildServiceProvider();
    }
}
