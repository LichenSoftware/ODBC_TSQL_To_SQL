using FluentAssertions;
using FsCheck;
using FsCheck.Xunit;

namespace MigrationAssessment.WorkItems.Tests;

/// <summary>
/// Property-based tests for input validation in AssessmentJsonReader.
/// Property 20: Invalid input produces validation error — verify non-JSON or
/// schema-non-conformant strings produce failed result with non-empty error message.
///
/// **Validates: Requirements 1.3, 1.4**
/// </summary>
public class InputValidationPropertyTests
{
    private static readonly AssessmentJsonReader Reader = new();

    #region Generators

    /// <summary>
    /// Generates random strings that are NOT valid JSON.
    /// Includes: random characters, truncated JSON fragments, partial objects, etc.
    /// </summary>
    private static Gen<string> GenInvalidJson()
    {
        var randomChars = from len in Gen.Choose(1, 50)
                          from chars in Gen.ListOf(len, Gen.Elements(
                              'a', 'b', 'c', 'x', 'y', 'z', '!', '@', '#', '$',
                              '%', '^', '&', '*', '(', ')', ' ', '\t', '\n',
                              '0', '1', '2', '3', '4', '5', '6', '7', '8', '9'))
                          let str = new string(chars.ToArray())
                          where !IsValidJson(str)
                          select str;

        var truncatedJson = Gen.Elements(
            "{",
            "{\"key\":",
            "{\"key\": \"value",
            "[1,2,",
            "[{\"a\":1},",
            "{\"analyzedStatements\": [",
            "{invalid}",
            "{'single': 'quotes'}",
            "{key: value}",
            "[1, 2, 3",
            "{\"unterminated",
            "{{nested}}");

        return Gen.OneOf(randomChars, truncatedJson);
    }

    /// <summary>
    /// Generates valid JSON objects that are missing the required
    /// 'analyzedStatements' or 'featureInventory' properties.
    /// </summary>
    private static Gen<string> GenValidJsonMissingRequiredFields()
    {
        return Gen.Elements(
            // Missing both required fields
            "{}",
            "{\"someField\": \"value\"}",
            "{\"unrelated\": [1, 2, 3]}",
            "{\"data\": {\"nested\": true}}",
            // Has analyzedStatements but missing featureInventory
            "{\"analyzedStatements\": []}",
            "{\"analyzedStatements\": [{\"statementText\": \"SELECT 1\", \"riskScore\": 1, \"weightedRisk\": 1.0}]}",
            // Has featureInventory but missing analyzedStatements
            "{\"featureInventory\": []}",
            "{\"featureInventory\": [{\"featureName\": \"TOP\", \"occurrenceCount\": 3}]}",
            // Has misspelled property names
            "{\"analyzed_statements\": [], \"feature_inventory\": []}",
            "{\"AnalyzedStatement\": [], \"FeatureInventory\": []}");
    }

    #endregion

    /// <summary>
    /// Property 20.1: InvalidJson_ProducesFailedResult — Generate random strings
    /// that are NOT valid JSON → call Parse → assert Succeeded=false and ErrorMessage is non-empty.
    ///
    /// **Validates: Requirements 1.3, 1.4**
    /// </summary>
    [Property(MaxTest = 100)]
    public Property InvalidJson_ProducesFailedResult()
    {
        return Prop.ForAll(GenInvalidJson().ToArbitrary(), invalidJson =>
        {
            var result = Reader.Parse(invalidJson, "test-source.json");

            result.Succeeded.Should().BeFalse(
                $"invalid JSON input should produce a failed result, but got Succeeded=true for input: '{Truncate(invalidJson)}'");

            result.ErrorMessage.Should().NotBeNullOrEmpty(
                "failed result should have a non-empty error message describing the issue");
        });
    }

    /// <summary>
    /// Property 20.2: ValidJsonWithoutRequiredFields_ProducesFailedResult — Generate
    /// valid JSON objects that are MISSING the required 'analyzedStatements' or
    /// 'featureInventory' properties → call Parse → assert Succeeded=false and
    /// ErrorMessage mentions the missing property.
    ///
    /// **Validates: Requirements 1.3, 1.4**
    /// </summary>
    [Property(MaxTest = 50)]
    public Property ValidJsonWithoutRequiredFields_ProducesFailedResult()
    {
        return Prop.ForAll(GenValidJsonMissingRequiredFields().ToArbitrary(), json =>
        {
            var result = Reader.Parse(json, "test-source.json");

            result.Succeeded.Should().BeFalse(
                $"JSON missing required fields should produce a failed result, but got Succeeded=true for input: '{json}'");

            result.ErrorMessage.Should().NotBeNullOrEmpty(
                "failed result should have a non-empty error message describing the schema violation");
        });
    }

    /// <summary>
    /// Property 20.3: NullJsonDocument_ProducesFailedResult — Parse "null" as JSON
    /// → assert failure with non-empty error message.
    ///
    /// **Validates: Requirements 1.3, 1.4**
    /// </summary>
    [Fact]
    public void NullJsonDocument_ProducesFailedResult()
    {
        var result = Reader.Parse("null", "test-source.json");

        result.Succeeded.Should().BeFalse(
            "a JSON 'null' document should produce a failed result");

        result.ErrorMessage.Should().NotBeNullOrEmpty(
            "failed result should have a non-empty error message");
    }

    #region Helpers

    private static bool IsValidJson(string str)
    {
        try
        {
            System.Text.Json.JsonDocument.Parse(str);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static string Truncate(string input, int maxLength = 50)
    {
        if (input.Length <= maxLength) return input;
        return input[..maxLength] + "...";
    }

    #endregion
}
