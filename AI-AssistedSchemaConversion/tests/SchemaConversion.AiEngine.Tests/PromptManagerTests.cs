using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace SchemaConversion.AiEngine.Tests;

public class PromptManagerTests
{
    [Fact]
    public void Constructor_LoadsTemplatesFromDirectory()
    {
        var promptDir = FindPromptsDirectory();

        var manager = new PromptManager(promptDir, NullLogger<PromptManager>.Instance);

        var categories = manager.GetAvailableCategories();
        Assert.NotEmpty(categories);
    }

    [Fact]
    public void GetTemplateVersion_ReturnsVersionString()
    {
        var promptDir = FindPromptsDirectory();
        var manager = new PromptManager(promptDir, NullLogger<PromptManager>.Instance);

        var version = manager.GetTemplateVersion("stored-procedure");

        Assert.Equal("1.0.0", version);
    }

    [Fact]
    public void GetTemplateVersion_FunctionTemplate_ReturnsVersion()
    {
        var promptDir = FindPromptsDirectory();
        var manager = new PromptManager(promptDir, NullLogger<PromptManager>.Instance);

        var version = manager.GetTemplateVersion("function");

        Assert.NotNull(version);
        Assert.NotEmpty(version);
    }

    [Fact]
    public void GetTemplateVersion_UnknownCategory_Throws()
    {
        var promptDir = FindPromptsDirectory();
        var manager = new PromptManager(promptDir, NullLogger<PromptManager>.Instance);

        Assert.Throws<InvalidOperationException>(() =>
            manager.GetTemplateVersion("nonexistent-category"));
    }

    [Fact]
    public void BuildPrompt_InjectsPlaceholders()
    {
        var promptDir = FindPromptsDirectory();
        var manager = new PromptManager(promptDir, NullLogger<PromptManager>.Instance);

        var placeholders = new Dictionary<string, string>
        {
            { "source_definition", "CREATE PROCEDURE dbo.Test AS BEGIN SELECT 1 END" },
            { "type_mapping_context", "INT -> INTEGER" },
            { "response_schema", "{}" }
        };

        var result = manager.BuildPrompt("stored-procedure", placeholders);

        Assert.Contains("CREATE PROCEDURE dbo.Test", result);
    }

    [Fact]
    public void BuildPrompt_UnknownCategory_Throws()
    {
        var promptDir = FindPromptsDirectory();
        var manager = new PromptManager(promptDir, NullLogger<PromptManager>.Instance);

        Assert.Throws<InvalidOperationException>(() =>
            manager.BuildPrompt("fake-category", new Dictionary<string, string>()));
    }

    [Fact]
    public void Constructor_ThrowsOnNonexistentDirectory()
    {
        Assert.Throws<DirectoryNotFoundException>(() =>
            new PromptManager("/nonexistent/path/prompts", NullLogger<PromptManager>.Instance));
    }

    [Fact]
    public void Constructor_ThrowsOnEmptyDirectory()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        Directory.CreateDirectory(tempDir);
        try
        {
            Assert.Throws<InvalidOperationException>(() =>
                new PromptManager(tempDir, NullLogger<PromptManager>.Instance));
        }
        finally
        {
            Directory.Delete(tempDir, true);
        }
    }

    [Fact]
    public void GetAvailableCategories_ContainsExpectedTemplates()
    {
        var promptDir = FindPromptsDirectory();
        var manager = new PromptManager(promptDir, NullLogger<PromptManager>.Instance);

        var categories = manager.GetAvailableCategories();

        Assert.Contains("stored-procedure", categories, StringComparer.OrdinalIgnoreCase);
        Assert.Contains("function", categories, StringComparer.OrdinalIgnoreCase);
        Assert.Contains("trigger", categories, StringComparer.OrdinalIgnoreCase);
        Assert.Contains("view", categories, StringComparer.OrdinalIgnoreCase);
    }

    private static string FindPromptsDirectory()
    {
        var dir = Directory.GetCurrentDirectory();
        while (dir is not null)
        {
            var promptsPath = Path.Combine(dir, "config", "prompts");
            if (Directory.Exists(promptsPath))
            {
                return promptsPath;
            }
            dir = Directory.GetParent(dir)?.FullName;
        }

        throw new InvalidOperationException(
            "Could not find config/prompts directory.");
    }
}
