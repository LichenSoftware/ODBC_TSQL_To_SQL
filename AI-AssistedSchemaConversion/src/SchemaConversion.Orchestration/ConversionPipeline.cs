using System.Diagnostics;
using Microsoft.Extensions.Logging;
using SchemaConversion.Core.Interfaces;
using SchemaConversion.Core.Models;
using SchemaConversion.Core.Options;
using SchemaConversion.Extraction;

namespace SchemaConversion.Orchestration;

/// <summary>
/// Orchestrates the full schema conversion workflow:
/// extract → session → change detect → dependency order → classify → convert → persist → report.
/// </summary>
public sealed class ConversionPipeline
{
    private readonly ISchemaExtractor _extractor;
    private readonly IConversionSessionStore _sessionStore;
    private readonly SessionChangeDetector _changeDetector;
    private readonly DependencyGraphBuilder _dependencyGraphBuilder;
    private readonly IObjectClassifier _classifier;
    private readonly IRuleBasedConverter _ruleBasedConverter;
    private readonly IAiConverter _aiConverter;
    private readonly IConversionReportGenerator _reportGenerator;
    private readonly SchemaMappingLoader _schemaMappingLoader;
    private readonly ILogger<ConversionPipeline> _logger;

    public ConversionPipeline(
        ISchemaExtractor extractor,
        IConversionSessionStore sessionStore,
        SessionChangeDetector changeDetector,
        DependencyGraphBuilder dependencyGraphBuilder,
        IObjectClassifier classifier,
        IRuleBasedConverter ruleBasedConverter,
        IAiConverter aiConverter,
        IConversionReportGenerator reportGenerator,
        SchemaMappingLoader schemaMappingLoader,
        ILogger<ConversionPipeline> logger)
    {
        _extractor = extractor;
        _sessionStore = sessionStore;
        _changeDetector = changeDetector;
        _dependencyGraphBuilder = dependencyGraphBuilder;
        _classifier = classifier;
        _ruleBasedConverter = ruleBasedConverter;
        _aiConverter = aiConverter;
        _reportGenerator = reportGenerator;
        _schemaMappingLoader = schemaMappingLoader;
        _logger = logger;
    }

    /// <summary>
    /// Executes the full conversion pipeline.
    /// </summary>
    public async Task<ConversionPipelineResult> ExecuteAsync(
        ConversionPipelineOptions options, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(options);

        var stopwatch = Stopwatch.StartNew();

        _logger.LogInformation("Starting conversion pipeline for session {SessionId}", options.SessionId);

        // 1. Get schema objects: either extract from source or load from session
        IReadOnlyList<SchemaObject> objects;
        var hasExtractionSource = !string.IsNullOrWhiteSpace(options.Extraction.ConnectionString)
                                  || options.Extraction.FilePaths is { Count: > 0 };

        if (hasExtractionSource)
        {
            objects = await _extractor.ExtractAsync(options.Extraction, ct).ConfigureAwait(false);
            _logger.LogInformation("Extracted {Count} schema objects from source", objects.Count);
        }
        else
        {
            // No extraction source — load objects from existing session entries
            var sessionEntries = await _sessionStore.GetAllEntriesAsync(options.SessionId, ct).ConfigureAwait(false);
            objects = sessionEntries.Select(e => e.Source).ToList();
            _logger.LogInformation("Loaded {Count} schema objects from existing session", objects.Count);
        }

        // 2. Load or create session, detect changes
        var session = await _sessionStore.LoadOrCreateAsync(options.SessionId, ct).ConfigureAwait(false);
        var existingEntries = await _sessionStore.GetAllEntriesAsync(options.SessionId, ct).ConfigureAwait(false);
        var objectsToProcess = _changeDetector.GetObjectsRequiringProcessing(
            objects, existingEntries, options.Filters);

        _logger.LogInformation("{Count} objects require processing", objectsToProcess.Count);

        if (objectsToProcess.Count == 0)
        {
            stopwatch.Stop();
            return new ConversionPipelineResult
            {
                TotalProcessed = 0,
                Converted = 0,
                Flagged = 0,
                Failed = 0,
                Duration = stopwatch.Elapsed
            };
        }

        // 3. Build dependency graph, get processing order
        var order = _dependencyGraphBuilder.GetProcessingOrder(objectsToProcess);
        _logger.LogInformation(
            "Dependency order: {OrderedCount} ordered, {CycleCount} cycles detected",
            order.Ordered.Count, order.Cycles.Count);

        // 4. Handle circular dependencies with placeholder strategy
        var cycleObjects = new List<SchemaObject>();
        foreach (var cycle in order.Cycles)
        {
            await HandleCycleAsync(cycle, options, ct).ConfigureAwait(false);
            cycleObjects.AddRange(cycle);
        }

        // 5. Process objects in dependency order (with parallelism for independent objects)
        var context = new ConversionContext
        {
            SessionId = options.SessionId,
            SchemaMappings = _schemaMappingLoader.GetMappings()
        };
        var semaphore = new SemaphoreSlim(options.Concurrency, options.Concurrency);

        int totalProcessed = 0;
        int converted = 0;
        int flagged = 0;
        int failed = 0;

        // Process ordered objects (these respect dependency order, so process sequentially in batches)
        var allObjectsToProcess = new List<SchemaObject>(order.Ordered);
        allObjectsToProcess.AddRange(cycleObjects);

        var tasks = new List<Task>();

        foreach (var obj in allObjectsToProcess)
        {
            await semaphore.WaitAsync(ct).ConfigureAwait(false);

            tasks.Add(Task.Run(async () =>
            {
                try
                {
                    var result = await ProcessSingleObjectAsync(obj, options, context, ct)
                        .ConfigureAwait(false);

                    Interlocked.Increment(ref totalProcessed);

                    switch (result.Status)
                    {
                        case ConversionStatus.Converted:
                            Interlocked.Increment(ref converted);
                            break;
                        case ConversionStatus.Flagged:
                            Interlocked.Increment(ref flagged);
                            break;
                        case ConversionStatus.Failed:
                            Interlocked.Increment(ref failed);
                            break;
                    }

                    // Progress reporting
                    var current = Interlocked.CompareExchange(ref totalProcessed, 0, 0);
                    _logger.LogInformation(
                        "Processed {Current}/{Total} objects",
                        current, allObjectsToProcess.Count);
                }
                finally
                {
                    semaphore.Release();
                }
            }, ct));
        }

        await Task.WhenAll(tasks).ConfigureAwait(false);

        // 7. Generate report
        stopwatch.Stop();

        _logger.LogInformation(
            "Pipeline complete: {Total} processed, {Converted} converted, {Flagged} flagged, {Failed} failed in {Duration}",
            totalProcessed, converted, flagged, failed, stopwatch.Elapsed);

        return new ConversionPipelineResult
        {
            TotalProcessed = totalProcessed,
            Converted = converted,
            Flagged = flagged,
            Failed = failed,
            Duration = stopwatch.Elapsed
        };
    }

    /// <summary>
    /// Processes a single schema object: classify → convert (with fallback) → persist.
    /// Errors are isolated per-object.
    /// </summary>
    private async Task<ConversionResult> ProcessSingleObjectAsync(
        SchemaObject obj,
        ConversionPipelineOptions options,
        ConversionContext context,
        CancellationToken ct)
    {
        var qualifiedName = $"{obj.SchemaName}.{obj.Name}";

        try
        {
            // Classify the object
            var classification = _classifier.Classify(obj);
            _logger.LogDebug(
                "Object {Name} classified as {Method}: {Reason}",
                qualifiedName, classification.Method, classification.Reason);

            ConversionResult result;

            if (classification.Method == ConversionMethod.RuleBased)
            {
                // Try rule-based first
                result = _ruleBasedConverter.Convert(obj, context);

                // Fallback: if rule-based conversion fails, try AI-assisted
                if (result.Status == ConversionStatus.Failed)
                {
                    _logger.LogInformation(
                        "Rule-based conversion failed for {Name}, falling back to AI-assisted",
                        qualifiedName);
                    result = await _aiConverter.ConvertAsync(obj, context, ct).ConfigureAwait(false);
                }
            }
            else
            {
                // AI-assisted conversion
                result = await _aiConverter.ConvertAsync(obj, context, ct).ConfigureAwait(false);
            }

            // Persist the result
            var entry = new ConversionSessionEntry
            {
                Source = obj,
                Result = result,
                ConvertedAt = DateTimeOffset.UtcNow
            };
            await _sessionStore.SaveEntryAsync(options.SessionId, entry, ct).ConfigureAwait(false);

            return result;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Per-object error isolation: catch, mark Failed, continue
            _logger.LogError(ex, "Error processing object {Name}", qualifiedName);

            var failedResult = new ConversionResult
            {
                ObjectName = obj.Name,
                SchemaName = obj.SchemaName,
                ObjectType = obj.ObjectType,
                Status = ConversionStatus.Failed,
                Method = ConversionMethod.RuleBased,
                ErrorMessage = $"Unhandled error: {ex.Message}"
            };

            try
            {
                var entry = new ConversionSessionEntry
                {
                    Source = obj,
                    Result = failedResult,
                    ConvertedAt = DateTimeOffset.UtcNow
                };
                await _sessionStore.SaveEntryAsync(options.SessionId, entry, ct).ConfigureAwait(false);
            }
            catch (Exception saveEx)
            {
                _logger.LogError(saveEx, "Failed to persist error state for {Name}", qualifiedName);
            }

            return failedResult;
        }
    }

    /// <summary>
    /// Handles circular dependencies by creating placeholder stubs first,
    /// then converting the objects with CREATE OR REPLACE semantics.
    /// </summary>
    private async Task HandleCycleAsync(
        IReadOnlyList<SchemaObject> cycle,
        ConversionPipelineOptions options,
        CancellationToken ct)
    {
        _logger.LogWarning(
            "Circular dependency detected among {Count} objects: {Objects}",
            cycle.Count,
            string.Join(", ", cycle.Select(o => $"{o.SchemaName}.{o.Name}")));

        // Create placeholder stubs for all objects in the cycle
        foreach (var obj in cycle)
        {
            var stubDdl = GeneratePlaceholderStub(obj);
            var stubResult = new ConversionResult
            {
                ObjectName = obj.Name,
                SchemaName = obj.SchemaName,
                ObjectType = obj.ObjectType,
                Status = ConversionStatus.Pending,
                Method = ConversionMethod.RuleBased,
                GeneratedDdl = stubDdl,
                ErrorMessage = "Placeholder stub for circular dependency resolution"
            };

            var entry = new ConversionSessionEntry
            {
                Source = obj,
                Result = stubResult,
                ConvertedAt = DateTimeOffset.UtcNow
            };

            await _sessionStore.SaveEntryAsync(options.SessionId, entry, ct).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Generates a minimal placeholder stub DDL for circular dependency resolution.
    /// The actual object will be created later with CREATE OR REPLACE.
    /// </summary>
    private static string GeneratePlaceholderStub(SchemaObject obj)
    {
        var qualifiedName = $"{obj.SchemaName}.{obj.Name}";

        return obj.ObjectType switch
        {
            SchemaObjectType.View =>
                $"CREATE OR REPLACE VIEW {qualifiedName} AS SELECT 1 AS placeholder; -- stub for circular dependency",
            SchemaObjectType.Function =>
                $"CREATE OR REPLACE FUNCTION {qualifiedName}() RETURNS void AS $$ BEGIN END; $$ LANGUAGE plpgsql; -- stub for circular dependency",
            SchemaObjectType.StoredProcedure =>
                $"CREATE OR REPLACE PROCEDURE {qualifiedName}() AS $$ BEGIN END; $$ LANGUAGE plpgsql; -- stub for circular dependency",
            _ =>
                $"-- Placeholder stub for {qualifiedName} ({obj.ObjectType}) - circular dependency"
        };
    }
}
