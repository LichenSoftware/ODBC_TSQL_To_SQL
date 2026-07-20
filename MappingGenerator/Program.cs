using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace MappingGenerator;

/// <summary>
/// Reads a schema conversion session and generates a procedure-mappings.json file
/// that PgPassthrough uses for custom SQL translation at runtime.
///
/// Usage:
///   dotnet run -- --session ..\AI-AssistedSchemaConversion\sessions\my-migration7 --output ..\PgPassthrough\src\PgPassthrough.Server\procedure-mappings.json
/// </summary>
public static class Program
{
    private static readonly JsonSerializerOptions ReadOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true
    };

    private static readonly JsonSerializerOptions WriteOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public static int Main(string[] args)
    {
        string? sessionPath = null;
        string? outputPath = null;

        for (int i = 0; i < args.Length; i++)
        {
            if (args[i] == "--session" && i + 1 < args.Length)
                sessionPath = args[++i];
            else if (args[i] == "--output" && i + 1 < args.Length)
                outputPath = args[++i];
        }

        if (string.IsNullOrEmpty(sessionPath))
        {
            Console.WriteLine("Usage: dotnet run -- --session <path-to-session> --output <output-file>");
            Console.WriteLine();
            Console.WriteLine("Example:");
            Console.WriteLine("  dotnet run -- --session ..\\AI-AssistedSchemaConversion\\sessions\\my-migration7 --output ..\\PgPassthrough\\src\\PgPassthrough.Server\\procedure-mappings.json");
            return 1;
        }

        outputPath ??= "procedure-mappings.json";
        sessionPath = Path.GetFullPath(sessionPath);
        outputPath = Path.GetFullPath(outputPath);

        Console.WriteLine($"Session: {sessionPath}");
        Console.WriteLine($"Output:  {outputPath}");

        var objectsPath = Path.Combine(sessionPath, "objects");
        if (!Directory.Exists(objectsPath))
        {
            Console.WriteLine($"Error: Objects directory not found: {objectsPath}");
            return 1;
        }

        var mappings = new List<ProcedureMapping>();

        // Process stored procedures and functions
        var files = Directory.GetFiles(objectsPath, "*.json")
            .Where(f => f.Contains(".StoredProcedure.json") || f.Contains(".Function.json"))
            .OrderBy(f => f);

        foreach (var file in files)
        {
            try
            {
                var json = File.ReadAllText(file);
                using var doc = JsonDocument.Parse(json);
                var root = doc.RootElement;

                var source = root.GetProperty("source");
                var result = root.GetProperty("result");

                var status = result.GetProperty("status").GetString();
                if (status != "converted" && status != "flagged") continue;

                var generatedDdl = result.TryGetProperty("generatedDdl", out var ddlProp)
                    ? ddlProp.GetString() ?? ""
                    : "";
                if (string.IsNullOrWhiteSpace(generatedDdl)) continue;

                var sourceName = source.GetProperty("name").GetString()!;
                var sourceSchema = source.GetProperty("schemaName").GetString()!;
                var sourceType = source.GetProperty("objectType").GetString()!;

                var mapping = BuildMapping(sourceSchema, sourceName, sourceType, generatedDdl);
                if (mapping != null)
                {
                    mappings.Add(mapping);
                    Console.WriteLine($"  ✓ {sourceSchema}.{sourceName} → {mapping.PostgresSchema}.{mapping.PostgresName} ({mapping.CallStyle})");
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"  Warning: Failed to process {Path.GetFileName(file)}: {ex.Message}");
            }
        }

        // Write output
        var manifest = new MappingManifest
        {
            Description = "Custom translation mappings generated from schema conversion session. " +
                         "PgPassthrough uses these to route EXEC/function calls to the correct PostgreSQL objects.",
            GeneratedAt = DateTimeOffset.UtcNow,
            SessionPath = sessionPath,
            Mappings = mappings
        };

        var outputJson = JsonSerializer.Serialize(manifest, WriteOptions);
        var outputDir = Path.GetDirectoryName(outputPath);
        if (!string.IsNullOrEmpty(outputDir)) Directory.CreateDirectory(outputDir);
        File.WriteAllText(outputPath, outputJson);

        Console.WriteLine();
        Console.WriteLine($"Generated {mappings.Count} mappings → {outputPath}");
        return 0;
    }

    private static ProcedureMapping? BuildMapping(string sourceSchema, string sourceName, string sourceType, string generatedDdl)
    {
        // Parse the target schema and function name from the generated DDL
        // Pattern: CREATE OR REPLACE FUNCTION schema.name(
        var funcMatch = Regex.Match(generatedDdl,
            @"CREATE\s+OR\s+REPLACE\s+FUNCTION\s+(\w+)\.(\w+)\s*\(",
            RegexOptions.IgnoreCase);

        // Pattern: CREATE OR REPLACE PROCEDURE schema.name(
        var procMatch = Regex.Match(generatedDdl,
            @"CREATE\s+OR\s+REPLACE\s+PROCEDURE\s+(\w+)\.(\w+)\s*\(",
            RegexOptions.IgnoreCase);

        string targetSchema, targetName, targetType;

        if (funcMatch.Success)
        {
            targetSchema = funcMatch.Groups[1].Value;
            targetName = funcMatch.Groups[2].Value;
            targetType = "function";
        }
        else if (procMatch.Success)
        {
            targetSchema = procMatch.Groups[1].Value;
            targetName = procMatch.Groups[2].Value;
            targetType = "procedure";
        }
        else
        {
            return null; // Can't determine target
        }

        // Extract parameters from the DDL
        var parameters = ExtractParameters(generatedDdl);

        // Determine call style
        var callStyle = targetType == "function" ? "SELECT" : "CALL";

        // Determine if it returns a table
        var returnsTable = generatedDdl.Contains("RETURNS TABLE", StringComparison.OrdinalIgnoreCase)
                        || generatedDdl.Contains("RETURNS SETOF", StringComparison.OrdinalIgnoreCase);

        return new ProcedureMapping
        {
            SourceSchema = sourceSchema,
            SourceName = sourceName,
            SourceType = sourceType,
            PostgresSchema = targetSchema,
            PostgresName = targetName,
            PostgresType = targetType,
            CallStyle = callStyle,
            ReturnsTable = returnsTable,
            Parameters = parameters
        };
    }

    private static List<ParameterMapping> ExtractParameters(string ddl)
    {
        var parameters = new List<ParameterMapping>();

        // Extract the parameter list between the first ( and the first )
        // that comes before RETURNS or LANGUAGE
        var paramSection = Regex.Match(ddl,
            @"(?:FUNCTION|PROCEDURE)\s+\w+\.\w+\s*\(([^)]*)\)\s*(?:RETURNS|LANGUAGE)",
            RegexOptions.IgnoreCase | RegexOptions.Singleline);

        if (!paramSection.Success || string.IsNullOrWhiteSpace(paramSection.Groups[1].Value))
            return parameters;

        var paramText = paramSection.Groups[1].Value;
        var paramLines = paramText.Split(',', StringSplitOptions.RemoveEmptyEntries);

        int position = 1;
        foreach (var line in paramLines)
        {
            var trimmed = line.Trim();
            // Pattern: param_name TYPE [DEFAULT value]
            var match = Regex.Match(trimmed, @"^(\w+)\s+(\w[\w\(\),\s]*?)(?:\s+DEFAULT\s+(.+))?$",
                RegexOptions.IgnoreCase);

            if (match.Success)
            {
                parameters.Add(new ParameterMapping
                {
                    PostgresName = match.Groups[1].Value,
                    PostgresType = match.Groups[2].Value.Trim(),
                    Position = position,
                    HasDefault = match.Groups[3].Success
                });
                position++;
            }
        }

        return parameters;
    }
}

// ─── Output Models ─────────────────────────────────────────────────────────────

public class MappingManifest
{
    public string Description { get; set; } = "";
    public DateTimeOffset GeneratedAt { get; set; }
    public string SessionPath { get; set; } = "";
    public List<ProcedureMapping> Mappings { get; set; } = [];
}

public class ProcedureMapping
{
    public string SourceSchema { get; set; } = "";
    public string SourceName { get; set; } = "";
    public string SourceType { get; set; } = "";
    public string PostgresSchema { get; set; } = "";
    public string PostgresName { get; set; } = "";
    public string PostgresType { get; set; } = "";
    public string CallStyle { get; set; } = "SELECT";  // "SELECT" or "CALL"
    public bool ReturnsTable { get; set; }
    public List<ParameterMapping> Parameters { get; set; } = [];
}

public class ParameterMapping
{
    public string PostgresName { get; set; } = "";
    public string PostgresType { get; set; } = "";
    public int Position { get; set; }
    public bool HasDefault { get; set; }
}
