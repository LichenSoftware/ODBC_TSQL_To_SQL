using Microsoft.Extensions.Logging;

namespace SchemaConversion.AiEngine;

/// <summary>
/// Loads versioned prompt templates from a directory, parses YAML frontmatter,
/// and constructs full prompts by injecting placeholders.
/// </summary>
public sealed class PromptManager
{
    private readonly Dictionary<string, PromptTemplate> _templates = new(StringComparer.OrdinalIgnoreCase);
    private readonly ILogger<PromptManager> _logger;

    public PromptManager(string templatesDirectory, ILogger<PromptManager> logger)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(templatesDirectory);
        _logger = logger;

        LoadTemplates(templatesDirectory);
        ValidateTemplates();
    }

    /// <summary>
    /// Gets the version string for a given template category.
    /// </summary>
    public string GetTemplateVersion(string category)
    {
        if (_templates.TryGetValue(category, out var template))
            return template.Version;

        throw new InvalidOperationException($"No template found for category '{category}'.");
    }

    /// <summary>
    /// Builds a full prompt by selecting the template for the given category
    /// and injecting the provided placeholders.
    /// </summary>
    public string BuildPrompt(string templateCategory, Dictionary<string, string> placeholders)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(templateCategory);
        ArgumentNullException.ThrowIfNull(placeholders);

        if (!_templates.TryGetValue(templateCategory, out var template))
        {
            throw new InvalidOperationException(
                $"No template found for category '{templateCategory}'. " +
                $"Available categories: {string.Join(", ", _templates.Keys)}");
        }

        var result = template.Body;

        foreach (var (key, value) in placeholders)
        {
            result = result.Replace($"{{{key}}}", value, StringComparison.OrdinalIgnoreCase);
        }

        _logger.LogDebug("Built prompt for category '{Category}' with version {Version}",
            templateCategory, template.Version);

        return result;
    }

    /// <summary>
    /// Gets all loaded template categories.
    /// </summary>
    public IReadOnlyCollection<string> GetAvailableCategories() => _templates.Keys;

    private void LoadTemplates(string directory)
    {
        if (!Directory.Exists(directory))
        {
            throw new DirectoryNotFoundException(
                $"Prompt templates directory not found: {directory}");
        }

        var files = Directory.GetFiles(directory, "*.md", SearchOption.TopDirectoryOnly);

        if (files.Length == 0)
        {
            throw new InvalidOperationException(
                $"No prompt template files (*.md) found in: {directory}");
        }

        foreach (var file in files)
        {
            try
            {
                var content = File.ReadAllText(file);
                var template = ParseTemplate(content, file);

                if (_templates.ContainsKey(template.Category))
                {
                    // Keep the highest version
                    var existing = _templates[template.Category];
                    if (string.Compare(template.Version, existing.Version, StringComparison.Ordinal) > 0)
                    {
                        _templates[template.Category] = template;
                        _logger.LogInformation(
                            "Updated template for category '{Category}' to version {Version} from {File}",
                            template.Category, template.Version, Path.GetFileName(file));
                    }
                }
                else
                {
                    _templates[template.Category] = template;
                    _logger.LogInformation(
                        "Loaded template for category '{Category}' version {Version} from {File}",
                        template.Category, template.Version, Path.GetFileName(file));
                }
            }
            catch (Exception ex) when (ex is not InvalidOperationException and not DirectoryNotFoundException)
            {
                _logger.LogWarning(ex, "Failed to parse template file: {File}", Path.GetFileName(file));
                throw new InvalidOperationException(
                    $"Failed to parse prompt template '{Path.GetFileName(file)}': {ex.Message}", ex);
            }
        }
    }

    private static PromptTemplate ParseTemplate(string content, string filePath)
    {
        const string frontmatterDelimiter = "---";

        var trimmed = content.TrimStart();
        if (!trimmed.StartsWith(frontmatterDelimiter, StringComparison.Ordinal))
        {
            throw new FormatException(
                $"Template '{Path.GetFileName(filePath)}' is missing YAML frontmatter (must start with ---).");
        }

        // Find the closing ---
        var firstDelimiterEnd = trimmed.IndexOf('\n') + 1;
        var secondDelimiterIndex = trimmed.IndexOf(
            frontmatterDelimiter, firstDelimiterEnd, StringComparison.Ordinal);

        if (secondDelimiterIndex < 0)
        {
            throw new FormatException(
                $"Template '{Path.GetFileName(filePath)}' has unclosed YAML frontmatter.");
        }

        var frontmatterContent = trimmed[firstDelimiterEnd..secondDelimiterIndex].Trim();
        var bodyStart = trimmed.IndexOf('\n', secondDelimiterIndex);
        var body = bodyStart >= 0 ? trimmed[(bodyStart + 1)..].TrimStart() : string.Empty;

        // Parse simple YAML key-value pairs from frontmatter
        var frontmatter = ParseSimpleYaml(frontmatterContent);

        if (!frontmatter.TryGetValue("version", out var version) || string.IsNullOrWhiteSpace(version))
        {
            throw new FormatException(
                $"Template '{Path.GetFileName(filePath)}' is missing required 'version' in frontmatter.");
        }

        if (!frontmatter.TryGetValue("category", out var category) || string.IsNullOrWhiteSpace(category))
        {
            throw new FormatException(
                $"Template '{Path.GetFileName(filePath)}' is missing required 'category' in frontmatter.");
        }

        return new PromptTemplate
        {
            Version = version,
            Category = category,
            Body = body,
            FilePath = filePath
        };
    }

    private static Dictionary<string, string> ParseSimpleYaml(string yaml)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        foreach (var line in yaml.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var colonIndex = line.IndexOf(':');
            if (colonIndex <= 0) continue;

            var key = line[..colonIndex].Trim();
            var value = line[(colonIndex + 1)..].Trim().Trim('"').Trim('\'');
            result[key] = value;
        }

        return result;
    }

    private void ValidateTemplates()
    {
        if (_templates.Count == 0)
        {
            throw new InvalidOperationException("No valid prompt templates were loaded.");
        }

        _logger.LogInformation("Validated {Count} prompt templates: {Categories}",
            _templates.Count, string.Join(", ", _templates.Keys));
    }

    private sealed class PromptTemplate
    {
        public required string Version { get; init; }
        public required string Category { get; init; }
        public required string Body { get; init; }
        public required string FilePath { get; init; }
    }
}
