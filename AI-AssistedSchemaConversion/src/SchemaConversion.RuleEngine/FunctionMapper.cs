using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using SchemaConversion.RuleEngine.Models;

namespace SchemaConversion.RuleEngine;

/// <summary>
/// Maps SQL Server functions to PostgreSQL equivalents using configuration from function-mappings.json.
/// Handles CONVERT with style codes, DATEDIFF/DATEADD patterns, and variable argument counts.
/// </summary>
public sealed partial class FunctionMapper
{
    private readonly Dictionary<string, FunctionMappingEntry> _functionMappings;
    private readonly Dictionary<string, DatePatternEntry> _datePatterns;
    private readonly Dictionary<string, StyleCodeEntry> _styleCodes;
    private readonly ConvertPatternsEntry? _convertPatterns;
    private readonly ILogger<FunctionMapper> _logger;

    /// <summary>
    /// Initializes a new instance of <see cref="FunctionMapper"/> by loading mappings from the specified JSON file.
    /// </summary>
    /// <param name="configFilePath">Path to the function-mappings.json configuration file.</param>
    /// <param name="logger">Logger instance for diagnostics.</param>
    /// <exception cref="InvalidOperationException">Thrown when the configuration file is missing, malformed, or contains invalid entries.</exception>
    public FunctionMapper(string configFilePath, ILogger<FunctionMapper> logger)
    {
        _logger = logger;
        var config = LoadAndValidateConfig(configFilePath);
        _functionMappings = BuildFunctionDictionary(config.Mappings);
        _datePatterns = BuildDatePatternDictionary(config.DatePatterns);
        _styleCodes = config.StyleCodes;
        _convertPatterns = config.ConvertPatterns;
        _logger.LogInformation(
            "FunctionMapper loaded {FuncCount} function mappings, {DateCount} date patterns, {StyleCount} style codes from {Path}",
            _functionMappings.Count, _datePatterns.Count, _styleCodes.Count, configFilePath);
    }

    /// <summary>
    /// Maps a SQL Server function call to its PostgreSQL equivalent expression.
    /// </summary>
    /// <param name="functionName">The SQL Server function name (e.g., "GETDATE", "ISNULL", "DATEDIFF").</param>
    /// <param name="args">The function arguments as translated expression strings.</param>
    /// <returns>The PostgreSQL expression string, or null if no mapping exists (signals AI fallback needed).</returns>
    public string? MapFunction(string functionName, IReadOnlyList<string> args)
    {
        var normalizedName = functionName.Trim().ToUpperInvariant();

        // Check date patterns first (DATEDIFF, DATEADD)
        if (_datePatterns.TryGetValue(normalizedName, out var datePattern))
        {
            return MapDatePattern(datePattern, args);
        }

        // Check CONVERT special handling
        if (normalizedName == "CONVERT" && args.Count >= 2)
        {
            return MapConvert(args);
        }

        // Check standard function mappings
        if (!_functionMappings.TryGetValue(normalizedName, out var entry))
        {
            _logger.LogDebug("No function mapping found for: {Function}", functionName);
            return null;
        }

        // Null postgresExpression means no direct equivalent
        if (entry.PostgresExpression is null)
        {
            _logger.LogDebug("Function {Function} has no direct PostgreSQL equivalent", functionName);
            return null;
        }

        // Validate argument count (argCount of -1 means variable)
        if (entry.ArgCount >= 0 && args.Count != entry.ArgCount)
        {
            _logger.LogWarning(
                "Function {Function} expected {Expected} args but got {Actual}",
                functionName, entry.ArgCount, args.Count);
            return null;
        }

        return SubstituteArgs(entry.PostgresExpression, args);
    }

    private string? MapDatePattern(DatePatternEntry pattern, IReadOnlyList<string> args)
    {
        if (args.Count < pattern.ArgCount)
        {
            _logger.LogWarning(
                "Date pattern {Function} expected {Expected} args but got {Actual}",
                pattern.SqlServerFunction, pattern.ArgCount, args.Count);
            return null;
        }

        // First argument is the date part (e.g., "YEAR", "DAY")
        var datePart = args[0].Trim().ToUpperInvariant().Trim('\'', '"');

        if (!pattern.PartMappings.TryGetValue(datePart, out var template))
        {
            _logger.LogWarning("Unsupported date part '{DatePart}' for {Function}", datePart, pattern.SqlServerFunction);
            return null;
        }

        // Substitute remaining args ({1}, {2}, etc.)
        var result = template;
        for (int i = 1; i < args.Count; i++)
        {
            result = result.Replace($"{{{i}}}", args[i]);
        }

        return result;
    }

    private string? MapConvert(IReadOnlyList<string> args)
    {
        if (_convertPatterns is null)
        {
            return null;
        }

        var targetType = args[0].Trim();
        var expression = args[1].Trim();

        // CONVERT without style code → simple CAST
        if (args.Count == 2)
        {
            return _convertPatterns.TypeOnly
                .Replace("{0}", targetType)
                .Replace("{1}", expression);
        }

        // CONVERT with style code
        var styleCodeStr = args[2].Trim();

        if (!_styleCodes.TryGetValue(styleCodeStr, out var styleCode))
        {
            _logger.LogWarning("Unknown CONVERT style code: {StyleCode}", styleCodeStr);
            return null;
        }

        // Determine output pattern based on target type
        var normalizedTargetType = targetType.ToUpperInvariant();

        if (IsDateTimeType(normalizedTargetType))
        {
            return _convertPatterns.ToTimestampWithStyleCode
                .Replace("{1}", expression)
                .Replace("{format}", styleCode.ToCharPattern);
        }

        if (normalizedTargetType.Contains("DATE") && !normalizedTargetType.Contains("TIME"))
        {
            return _convertPatterns.ToDateWithStyleCode
                .Replace("{1}", expression)
                .Replace("{format}", styleCode.ToCharPattern);
        }

        // Default: convert to string with format
        return _convertPatterns.WithStyleCode
            .Replace("{1}", expression)
            .Replace("{format}", styleCode.ToCharPattern);
    }

    private static bool IsDateTimeType(string type)
    {
        return type.Contains("DATETIME") || type.Contains("TIMESTAMP") || type == "SMALLDATETIME";
    }

    private static string SubstituteArgs(string template, IReadOnlyList<string> args)
    {
        var result = template;

        // Handle variable args ({args} placeholder) — join all args with comma
        if (result.Contains("{args}"))
        {
            result = result.Replace("{args}", string.Join(", ", args));
            return result;
        }

        // Handle positional args ({0}, {1}, {2}, etc.)
        for (int i = 0; i < args.Count; i++)
        {
            result = result.Replace($"{{{i}}}", args[i]);
        }

        return result;
    }

    private static Dictionary<string, FunctionMappingEntry> BuildFunctionDictionary(List<FunctionMappingEntry> entries)
    {
        var dict = new Dictionary<string, FunctionMappingEntry>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in entries)
        {
            dict[entry.SqlServerFunction.ToUpperInvariant()] = entry;
        }
        return dict;
    }

    private static Dictionary<string, DatePatternEntry> BuildDatePatternDictionary(List<DatePatternEntry> entries)
    {
        var dict = new Dictionary<string, DatePatternEntry>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in entries)
        {
            dict[entry.SqlServerFunction.ToUpperInvariant()] = entry;
        }
        return dict;
    }

    private FunctionMappingConfig LoadAndValidateConfig(string configFilePath)
    {
        if (!File.Exists(configFilePath))
        {
            throw new InvalidOperationException($"Function mappings configuration file not found: {configFilePath}");
        }

        string json;
        try
        {
            json = File.ReadAllText(configFilePath);
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException($"Failed to read function mappings configuration file: {configFilePath}", ex);
        }

        FunctionMappingConfig? config;
        try
        {
            config = JsonSerializer.Deserialize<FunctionMappingConfig>(json);
        }
        catch (JsonException ex)
        {
            throw new InvalidOperationException($"Function mappings configuration file is not valid JSON: {configFilePath}", ex);
        }

        if (config is null)
        {
            throw new InvalidOperationException($"Function mappings configuration file deserialized to null: {configFilePath}");
        }

        // Validate required fields
        foreach (var entry in config.Mappings)
        {
            if (string.IsNullOrWhiteSpace(entry.SqlServerFunction))
            {
                throw new InvalidOperationException("Function mapping entry is missing required field 'sqlServerFunction'.");
            }
        }

        foreach (var entry in config.DatePatterns)
        {
            if (string.IsNullOrWhiteSpace(entry.SqlServerFunction))
            {
                throw new InvalidOperationException("Date pattern entry is missing required field 'sqlServerFunction'.");
            }

            if (entry.PartMappings.Count == 0)
            {
                throw new InvalidOperationException(
                    $"Date pattern entry for '{entry.SqlServerFunction}' has no part mappings defined.");
            }
        }

        return config;
    }
}
