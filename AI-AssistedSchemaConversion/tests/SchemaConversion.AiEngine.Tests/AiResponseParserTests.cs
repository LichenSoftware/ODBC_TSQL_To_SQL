using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace SchemaConversion.AiEngine.Tests;

public class AiResponseParserTests
{
    private readonly AiResponseParser _parser;

    public AiResponseParserTests()
    {
        _parser = new AiResponseParser(NullLogger<AiResponseParser>.Instance);
    }

    [Fact]
    public void Parse_ValidResponse_ReturnsSuccess()
    {
        var json = """
            {
                "ddl": "CREATE OR REPLACE FUNCTION get_orders() RETURNS void AS $$ BEGIN END; $$ LANGUAGE plpgsql;",
                "wrapperDdl": null,
                "confidence": 0.92,
                "assumptions": ["Assumed result set always returns exactly 5 columns"],
                "reviewAreas": [
                    {
                        "codeSection": "Lines 15-22",
                        "reason": "Complex string interpolation"
                    }
                ],
                "compatibilityNotes": [
                    {
                        "category": "ErrorHandling",
                        "description": "RAISERROR severity levels do not map directly"
                    }
                ]
            }
            """;

        var result = _parser.Parse(json);

        Assert.True(result.IsSuccess);
        Assert.NotNull(result.Response);
        Assert.Contains("CREATE OR REPLACE FUNCTION", result.Response.Ddl);
        Assert.Equal(0.92, result.Response.Confidence);
        Assert.Single(result.Response.Assumptions);
        Assert.Single(result.Response.ReviewAreas);
        Assert.Single(result.Response.CompatibilityNotes);
    }

    [Fact]
    public void Parse_ResponseInMarkdownCodeFence_ExtractsJson()
    {
        var response = """
            Here's the converted function:
            
            ```json
            {
                "ddl": "CREATE FUNCTION test() RETURNS void AS $$ BEGIN END; $$ LANGUAGE plpgsql;",
                "confidence": 0.85,
                "assumptions": [],
                "reviewAreas": []
            }
            ```
            """;

        var result = _parser.Parse(response);

        Assert.True(result.IsSuccess);
        Assert.NotNull(result.Response);
        Assert.Equal(0.85, result.Response.Confidence);
    }

    [Fact]
    public void Parse_EmptyString_ReturnsFailure()
    {
        var result = _parser.Parse("");

        Assert.False(result.IsSuccess);
        Assert.NotNull(result.ErrorMessage);
    }

    [Fact]
    public void Parse_NullWhitespace_ReturnsFailure()
    {
        var result = _parser.Parse("   ");

        Assert.False(result.IsSuccess);
        Assert.NotNull(result.ErrorMessage);
    }

    [Fact]
    public void Parse_InvalidJson_ReturnsFailure()
    {
        var result = _parser.Parse("this is not json at all");

        Assert.False(result.IsSuccess);
        Assert.NotNull(result.ErrorMessage);
    }

    [Fact]
    public void Parse_MissingDdlField_ReturnsFailure()
    {
        var json = """
            {
                "confidence": 0.8,
                "assumptions": [],
                "reviewAreas": []
            }
            """;

        var result = _parser.Parse(json);

        Assert.False(result.IsSuccess);
        Assert.Contains("ddl", result.ErrorMessage, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Parse_MissingConfidence_ReturnsFailure()
    {
        var json = """
            {
                "ddl": "CREATE TABLE test (id INTEGER)",
                "assumptions": [],
                "reviewAreas": []
            }
            """;

        var result = _parser.Parse(json);

        Assert.False(result.IsSuccess);
        Assert.Contains("confidence", result.ErrorMessage, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Parse_MissingAssumptions_ReturnsFailure()
    {
        var json = """
            {
                "ddl": "CREATE TABLE test (id INTEGER)",
                "confidence": 0.9,
                "reviewAreas": []
            }
            """;

        var result = _parser.Parse(json);

        Assert.False(result.IsSuccess);
        Assert.Contains("assumptions", result.ErrorMessage, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Parse_MissingReviewAreas_ReturnsFailure()
    {
        var json = """
            {
                "ddl": "CREATE TABLE test (id INTEGER)",
                "confidence": 0.9,
                "assumptions": []
            }
            """;

        var result = _parser.Parse(json);

        Assert.False(result.IsSuccess);
        Assert.Contains("reviewAreas", result.ErrorMessage, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Parse_ConfidenceOutOfRange_ReturnsFailure()
    {
        var json = """
            {
                "ddl": "CREATE TABLE test (id INTEGER)",
                "confidence": 1.5,
                "assumptions": [],
                "reviewAreas": []
            }
            """;

        var result = _parser.Parse(json);

        Assert.False(result.IsSuccess);
        Assert.Contains("confidence", result.ErrorMessage, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Parse_WithWrapperDdl_IncludesWrapper()
    {
        var json = """
            {
                "ddl": "CREATE FUNCTION get_data() RETURNS TABLE(id INTEGER) AS $$ BEGIN RETURN QUERY SELECT 1; END; $$ LANGUAGE plpgsql;",
                "wrapperDdl": "CREATE VIEW get_data_compat AS SELECT * FROM get_data();",
                "confidence": 0.88,
                "assumptions": [],
                "reviewAreas": []
            }
            """;

        var result = _parser.Parse(json);

        Assert.True(result.IsSuccess);
        Assert.NotNull(result.Response!.WrapperDdl);
        Assert.Contains("get_data_compat", result.Response.WrapperDdl);
    }

    [Fact]
    public void Parse_MinimalValidResponse_Succeeds()
    {
        var json = """
            {
                "ddl": "SELECT 1",
                "confidence": 0.0,
                "assumptions": [],
                "reviewAreas": []
            }
            """;

        var result = _parser.Parse(json);

        Assert.True(result.IsSuccess);
        Assert.Equal(0.0, result.Response!.Confidence);
    }
}
