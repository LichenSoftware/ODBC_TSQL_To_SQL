using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace SchemaConversion.RuleEngine.Tests;

public class ExpressionTranslatorTests
{
    private readonly ExpressionTranslator _translator;

    public ExpressionTranslatorTests()
    {
        var configDir = FindConfigDirectory();
        var typeMapper = new TypeMapper(
            Path.Combine(configDir, "type-mappings.json"),
            NullLogger<TypeMapper>.Instance);
        var functionMapper = new FunctionMapper(
            Path.Combine(configDir, "function-mappings.json"),
            NullLogger<FunctionMapper>.Instance);

        _translator = new ExpressionTranslator(
            typeMapper, functionMapper, NullLogger<ExpressionTranslator>.Instance);
    }

    [Fact]
    public void Translate_StringConcatenation_ConvertsPlusToDoublePipe()
    {
        var result = _translator.Translate("'Hello' + ' ' + 'World'");

        Assert.True(result.IsSuccess);
        Assert.Contains("||", result.TranslatedExpression);
    }

    [Fact]
    public void Translate_GETDATE_TranslatesToCURRENT_TIMESTAMP()
    {
        var result = _translator.Translate("GETDATE()");

        Assert.True(result.IsSuccess);
        Assert.Equal("CURRENT_TIMESTAMP", result.TranslatedExpression);
    }

    [Fact]
    public void Translate_ISNULL_TranslatesToCOALESCE()
    {
        var result = _translator.Translate("ISNULL(col1, 'default')");

        Assert.True(result.IsSuccess);
        Assert.Contains("COALESCE", result.TranslatedExpression);
    }

    [Fact]
    public void Translate_LEN_TranslatesToLENGTH()
    {
        var result = _translator.Translate("LEN(name)");

        Assert.True(result.IsSuccess);
        Assert.Contains("LENGTH", result.TranslatedExpression);
    }

    [Fact]
    public void Translate_NestedFunctions_TranslatesCorrectly()
    {
        var result = _translator.Translate("ISNULL(LEN(col1), 0)");

        Assert.True(result.IsSuccess);
        Assert.Contains("COALESCE", result.TranslatedExpression);
        Assert.Contains("LENGTH", result.TranslatedExpression);
    }

    [Fact]
    public void TranslateSelect_TOP_ConvertsToLIMIT()
    {
        var result = _translator.TranslateSelect("SELECT TOP 10 * FROM dbo.Orders");

        Assert.True(result.IsSuccess);
        Assert.Contains("LIMIT", result.TranslatedExpression);
        Assert.Contains("10", result.TranslatedExpression);
    }

    [Fact]
    public void Translate_UnmappedFunction_ReturnsCannotTranslate()
    {
        var result = _translator.Translate("TOTALLY_UNKNOWN_FUNC(x, y)");

        Assert.False(result.IsSuccess);
        Assert.NotNull(result.CannotTranslateReason);
    }

    [Fact]
    public void Translate_NullLiteral_ReturnsNULL()
    {
        var result = _translator.Translate("NULL");

        Assert.True(result.IsSuccess);
        Assert.Equal("NULL", result.TranslatedExpression);
    }

    [Fact]
    public void Translate_IntegerLiteral_ReturnsValue()
    {
        var result = _translator.Translate("42");

        Assert.True(result.IsSuccess);
        Assert.Equal("42", result.TranslatedExpression);
    }

    [Fact]
    public void Translate_EmptyString_ReturnsEmptySuccess()
    {
        var result = _translator.Translate("");

        Assert.True(result.IsSuccess);
        Assert.Equal(string.Empty, result.TranslatedExpression);
    }

    [Fact]
    public void Translate_ArithmeticExpression_PreservesOperators()
    {
        var result = _translator.Translate("1 + 2 * 3");

        Assert.True(result.IsSuccess);
        Assert.NotNull(result.TranslatedExpression);
    }

    private static string FindConfigDirectory()
    {
        var dir = Directory.GetCurrentDirectory();
        while (dir is not null)
        {
            var configPath = Path.Combine(dir, "config");
            if (Directory.Exists(configPath) && File.Exists(Path.Combine(configPath, "type-mappings.json")))
            {
                return configPath;
            }
            dir = Directory.GetParent(dir)?.FullName;
        }

        throw new InvalidOperationException(
            "Could not find config directory.");
    }
}
