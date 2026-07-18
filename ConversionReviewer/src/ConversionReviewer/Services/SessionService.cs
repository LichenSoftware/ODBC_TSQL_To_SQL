using System.Text.Json;
using ConversionReviewer.Models;
using Microsoft.AspNetCore.Hosting;

namespace ConversionReviewer.Services;

/// <summary>
/// Loads and manages conversion session data from the AI-AssistedSchemaConversion output files.
/// </summary>
public class SessionService
{
    private readonly string _sessionsBasePath;
    private readonly JsonSerializerOptions _jsonOptions;

    public SessionService(IConfiguration configuration, IWebHostEnvironment env)
    {
        var configuredPath = configuration["SessionsPath"];
        if (!string.IsNullOrEmpty(configuredPath))
        {
            _sessionsBasePath = Path.IsPathRooted(configuredPath)
                ? configuredPath
                : Path.GetFullPath(Path.Combine(env.ContentRootPath, configuredPath));
        }
        else
        {
            _sessionsBasePath = Path.GetFullPath(Path.Combine(env.ContentRootPath, "..", "..", "..", "AI-AssistedSchemaConversion", "sessions"));
        }

        _jsonOptions = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            WriteIndented = true
        };
    }

    /// <summary>
    /// Returns all available session folder names.
    /// </summary>
    public List<string> GetAvailableSessions()
    {
        if (!Directory.Exists(_sessionsBasePath))
            return [];

        return Directory.GetDirectories(_sessionsBasePath)
            .Select(Path.GetFileName)
            .Where(n => n != null)
            .Cast<string>()
            .OrderBy(n => n)
            .ToList();
    }

    /// <summary>
    /// Loads all objects from a session, sorted in dependency order.
    /// </summary>
    public async Task<List<ConversionObject>> LoadSessionAsync(string sessionName)
    {
        var objectsPath = Path.Combine(_sessionsBasePath, sessionName, "objects");
        if (!Directory.Exists(objectsPath))
            return [];

        var objects = new List<ConversionObject>();

        foreach (var file in Directory.GetFiles(objectsPath, "*.json"))
        {
            var json = await File.ReadAllTextAsync(file);
            var obj = JsonSerializer.Deserialize<ConversionObject>(json, _jsonOptions);
            if (obj != null)
            {
                obj.FileName = Path.GetFileName(file);
                obj.FullPath = file;
                objects.Add(obj);
            }
        }

        return TopologicalSort(objects);
    }

    /// <summary>
    /// Saves the modified object back to its JSON file (preserves all fields).
    /// </summary>
    public async Task SaveObjectAsync(ConversionObject obj)
    {
        var json = JsonSerializer.Serialize(obj, _jsonOptions);
        await File.WriteAllTextAsync(obj.FullPath, json);
    }

    /// <summary>
    /// Sorts objects in dependency order using topological sort.
    /// Tables first (in dependency order), then views, functions, procedures, triggers, synonyms.
    /// </summary>
    private static List<ConversionObject> TopologicalSort(List<ConversionObject> objects)
    {
        // Build a lookup by qualified name (e.g., "dbo.Categories")
        var byName = objects.ToDictionary(
            o => $"{o.Source.SchemaName}.{o.Source.Name}",
            o => o,
            StringComparer.OrdinalIgnoreCase);

        var sorted = new List<ConversionObject>();
        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var visiting = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // Assign type priority for tie-breaking
        static int TypePriority(string objectType) => objectType.ToLowerInvariant() switch
        {
            "table" => 0,
            "function" => 1,
            "view" => 2,
            "storedprocedure" => 3,
            "trigger" => 4,
            "synonym" => 5,
            _ => 6
        };

        // Process in type-priority order for stable output
        var ordered = objects.OrderBy(o => TypePriority(o.Source.ObjectType))
                            .ThenBy(o => o.Source.Name);

        void Visit(ConversionObject obj)
        {
            var key = $"{obj.Source.SchemaName}.{obj.Source.Name}";

            if (visited.Contains(key))
                return;

            if (visiting.Contains(key))
                return; // Circular dependency, break the cycle

            visiting.Add(key);

            // Visit dependencies first
            foreach (var dep in obj.Source.DependsOn)
            {
                if (byName.TryGetValue(dep, out var depObj))
                {
                    Visit(depObj);
                }
            }

            visiting.Remove(key);
            visited.Add(key);
            sorted.Add(obj);
        }

        foreach (var obj in ordered)
        {
            Visit(obj);
        }

        return sorted;
    }
}
