using FluentAssertions;
using MigrationAssessment.WorkItems;

namespace MigrationAssessment.WorkItems.Tests;

public class AssessmentJsonReaderTests
{
    private readonly AssessmentJsonReader _reader = new();

    private static string GetTestAssessmentPath()
    {
        // Walk up from bin/Debug/net8.0 to MigrationAssessment root
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "test-assessment.json")))
        {
            dir = dir.Parent;
        }
        return dir != null
            ? Path.Combine(dir.FullName, "test-assessment.json")
            : throw new FileNotFoundException("test-assessment.json not found walking up from " + AppContext.BaseDirectory);
    }

    [Fact]
    public async Task ReadAsync_FileNotFound_ReturnsError()
    {
        var nonExistentPath = Path.Combine(Path.GetTempPath(), "does-not-exist-assessment.json");

        var result = await _reader.ReadAsync(nonExistentPath, CancellationToken.None);

        result.Succeeded.Should().BeFalse();
        result.ErrorMessage.Should().Contain(nonExistentPath);
        result.ErrorMessage.Should().Contain("not found");
    }

    [Fact]
    public void Parse_InvalidJson_ReturnsError()
    {
        var result = _reader.Parse("not valid json", "test.json");

        result.Succeeded.Should().BeFalse();
        result.ErrorMessage.Should().NotBeNullOrEmpty();
        result.ErrorMessage.Should().Contain("Invalid JSON");
    }

    [Fact]
    public void Parse_ValidJsonMissingAnalyzedStatements_ReturnsError()
    {
        var result = _reader.Parse("""{"featureInventory":[]}""", "test.json");

        result.Succeeded.Should().BeFalse();
        result.ErrorMessage.Should().Contain("analyzedStatements");
    }

    [Fact]
    public void Parse_ValidJsonMissingFeatureInventory_ReturnsError()
    {
        var result = _reader.Parse("""{"analyzedStatements":[]}""", "test.json");

        result.Succeeded.Should().BeFalse();
        result.ErrorMessage.Should().Contain("featureInventory");
    }

    [Fact]
    public void Parse_EmptyAssessment_ReturnsSuccessWithEmptyData()
    {
        var result = _reader.Parse("""{"analyzedStatements":[],"featureInventory":[]}""", "test.json");

        result.Succeeded.Should().BeTrue();
        result.Statements.Should().NotBeNull();
        result.Statements.Should().BeEmpty();
        result.ErrorMessage.Should().NotBeNullOrEmpty("empty assessments should include an informational message");
    }

    [Fact]
    public async Task ReadAsync_ValidFile_ParsesSuccessfully()
    {
        var path = GetTestAssessmentPath();

        var result = await _reader.ReadAsync(path, CancellationToken.None);

        result.Succeeded.Should().BeTrue();
        result.Statements.Should().NotBeNull();
        result.Statements.Should().NotBeEmpty();
    }
}
