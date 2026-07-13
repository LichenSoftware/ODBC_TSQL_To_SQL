using Microsoft.Extensions.Logging.Abstractions;
using SchemaConversion.RuleEngine.Models;
using Xunit;

namespace SchemaConversion.RuleEngine.Tests;

public class TypeMapperTests
{
    private readonly TypeMapper _mapper;

    public TypeMapperTests()
    {
        var configDir = FindConfigDirectory();
        _mapper = new TypeMapper(
            Path.Combine(configDir, "type-mappings.json"),
            NullLogger<TypeMapper>.Instance);
    }

    [Fact]
    public void MapType_INT_ReturnsINTEGER()
    {
        var result = _mapper.MapType("INT");

        Assert.Equal("INTEGER", result.MappedType);
        Assert.Null(result.AdditionalConstraint);
        Assert.False(result.RequiresManualReview);
    }

    [Fact]
    public void MapType_BIGINT_ReturnsBIGINT()
    {
        var result = _mapper.MapType("BIGINT");

        Assert.Equal("BIGINT", result.MappedType);
        Assert.Null(result.AdditionalConstraint);
    }

    [Fact]
    public void MapType_SMALLINT_ReturnsSMALLINT()
    {
        var result = _mapper.MapType("SMALLINT");

        Assert.Equal("SMALLINT", result.MappedType);
        Assert.Null(result.AdditionalConstraint);
    }

    [Fact]
    public void MapType_TINYINT_ReturnsSMALLINT_WithCheckConstraint()
    {
        var result = _mapper.MapType("TINYINT");

        Assert.Equal("SMALLINT", result.MappedType);
        Assert.NotNull(result.AdditionalConstraint);
        Assert.Contains("0", result.AdditionalConstraint);
        Assert.Contains("255", result.AdditionalConstraint);
    }

    [Fact]
    public void MapType_BIT_ReturnsBOOLEAN()
    {
        var result = _mapper.MapType("BIT");

        Assert.Equal("BOOLEAN", result.MappedType);
        Assert.Null(result.AdditionalConstraint);
    }

    [Fact]
    public void MapType_MONEY_ReturnsNUMERIC19_4()
    {
        var result = _mapper.MapType("MONEY");

        Assert.Equal("NUMERIC(19,4)", result.MappedType);
    }

    [Fact]
    public void MapType_SMALLMONEY_ReturnsNUMERIC10_4()
    {
        var result = _mapper.MapType("SMALLMONEY");

        Assert.Equal("NUMERIC(10,4)", result.MappedType);
    }

    [Fact]
    public void MapType_FLOAT_ReturnsDOUBLE_PRECISION()
    {
        var result = _mapper.MapType("FLOAT");

        Assert.Equal("DOUBLE PRECISION", result.MappedType);
    }

    [Fact]
    public void MapType_REAL_ReturnsREAL()
    {
        var result = _mapper.MapType("REAL");

        Assert.Equal("REAL", result.MappedType);
    }

    [Fact]
    public void MapType_DATETIME2_ReturnsTIMESTAMP_WithPrecision()
    {
        var result = _mapper.MapType("DATETIME2", precision: 3);

        Assert.Equal("TIMESTAMP(3)", result.MappedType);
    }

    [Fact]
    public void MapType_DATETIME2_CapsAt6Precision()
    {
        var result = _mapper.MapType("DATETIME2", precision: 7);

        Assert.Equal("TIMESTAMP(6)", result.MappedType);
    }

    [Fact]
    public void MapType_DATETIME_ReturnsTIMESTAMP3()
    {
        var result = _mapper.MapType("DATETIME");

        Assert.Equal("TIMESTAMP(3)", result.MappedType);
    }

    [Fact]
    public void MapType_UNIQUEIDENTIFIER_ReturnsUUID()
    {
        var result = _mapper.MapType("UNIQUEIDENTIFIER");

        Assert.Equal("UUID", result.MappedType);
    }

    [Fact]
    public void MapType_NVARCHAR_WithLength_ReturnsVARCHAR()
    {
        var result = _mapper.MapType("NVARCHAR", length: 100);

        Assert.Equal("VARCHAR(100)", result.MappedType);
    }

    [Fact]
    public void MapType_NVARCHAR_MAX_ReturnsTEXT()
    {
        var result = _mapper.MapType("NVARCHAR", length: -1);

        Assert.Equal("TEXT", result.MappedType);
    }

    [Fact]
    public void MapType_DECIMAL_PreservesPrecisionAndScale()
    {
        var result = _mapper.MapType("DECIMAL", precision: 18, scale: 2);

        Assert.Equal("NUMERIC(18,2)", result.MappedType);
    }

    [Fact]
    public void MapType_VARBINARY_ReturnsBYTEA()
    {
        var result = _mapper.MapType("VARBINARY");

        Assert.Equal("BYTEA", result.MappedType);
    }

    [Fact]
    public void MapType_XML_ReturnsXML()
    {
        var result = _mapper.MapType("XML");

        Assert.Equal("XML", result.MappedType);
    }

    [Fact]
    public void MapType_HIERARCHYID_RequiresManualReview()
    {
        var result = _mapper.MapType("HIERARCHYID");

        Assert.True(result.RequiresManualReview);
        Assert.NotNull(result.CompatibilityNote);
    }

    [Fact]
    public void MapType_UnknownType_RequiresManualReview()
    {
        var result = _mapper.MapType("NOSUCHTYPE");

        Assert.True(result.RequiresManualReview);
        Assert.Null(result.MappedType);
    }

    [Fact]
    public void MapType_IsCaseInsensitive()
    {
        var result = _mapper.MapType("int");

        Assert.Equal("INTEGER", result.MappedType);
    }

    [Fact]
    public void Constructor_ThrowsOnMissingConfigFile()
    {
        Assert.Throws<InvalidOperationException>(() =>
            new TypeMapper("nonexistent.json", NullLogger<TypeMapper>.Instance));
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
