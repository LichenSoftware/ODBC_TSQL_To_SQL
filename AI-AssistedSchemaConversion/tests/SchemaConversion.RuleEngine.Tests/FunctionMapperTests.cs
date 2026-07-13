using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace SchemaConversion.RuleEngine.Tests;

public class FunctionMapperTests
{
    private readonly FunctionMapper _mapper;

    public FunctionMapperTests()
    {
        var configDir = FindConfigDirectory();
        _mapper = new FunctionMapper(
            Path.Combine(configDir, "function-mappings.json"),
            NullLogger<FunctionMapper>.Instance);
    }

    [Fact]
    public void MapFunction_GETDATE_ReturnsCURRENT_TIMESTAMP()
    {
        var result = _mapper.MapFunction("GETDATE", []);

        Assert.Equal("CURRENT_TIMESTAMP", result);
    }

    [Fact]
    public void MapFunction_SYSDATETIME_ReturnsCURRENT_TIMESTAMP()
    {
        var result = _mapper.MapFunction("SYSDATETIME", []);

        Assert.Equal("CURRENT_TIMESTAMP", result);
    }

    [Fact]
    public void MapFunction_ISNULL_ReturnsCOALESCE()
    {
        var result = _mapper.MapFunction("ISNULL", ["col1", "'default'"]);

        Assert.Equal("COALESCE(col1, 'default')", result);
    }

    [Fact]
    public void MapFunction_LEN_ReturnsLENGTH()
    {
        var result = _mapper.MapFunction("LEN", ["col1"]);

        Assert.Equal("LENGTH(col1)", result);
    }

    [Fact]
    public void MapFunction_CHARINDEX_ReturnsPOSITION()
    {
        var result = _mapper.MapFunction("CHARINDEX", ["'abc'", "col1"]);

        Assert.Equal("POSITION('abc' IN col1)", result);
    }

    [Fact]
    public void MapFunction_NEWID_ReturnsGen_random_uuid()
    {
        var result = _mapper.MapFunction("NEWID", []);

        Assert.Equal("gen_random_uuid()", result);
    }

    [Fact]
    public void MapFunction_SCOPE_IDENTITY_ReturnsLastval()
    {
        var result = _mapper.MapFunction("SCOPE_IDENTITY", []);

        Assert.Equal("lastval()", result);
    }

    [Fact]
    public void MapFunction_DB_NAME_ReturnsCurrent_database()
    {
        var result = _mapper.MapFunction("DB_NAME", []);

        Assert.Equal("current_database()", result);
    }

    [Fact]
    public void MapFunction_DATEDIFF_DAY_ReturnsExtract()
    {
        var result = _mapper.MapFunction("DATEDIFF", ["DAY", "start_date", "end_date"]);

        Assert.NotNull(result);
        Assert.Contains("end_date", result);
        Assert.Contains("start_date", result);
        Assert.Contains("EXTRACT", result);
    }

    [Fact]
    public void MapFunction_DATEADD_MONTH_ReturnsIntervalArithmetic()
    {
        var result = _mapper.MapFunction("DATEADD", ["MONTH", "3", "order_date"]);

        Assert.NotNull(result);
        Assert.Contains("order_date", result);
        Assert.Contains("INTERVAL", result);
        Assert.Contains("months", result);
    }

    [Fact]
    public void MapFunction_CONVERT_WithStyleCode101_ReturnsToChar()
    {
        var result = _mapper.MapFunction("CONVERT", ["VARCHAR(10)", "order_date", "101"]);

        Assert.NotNull(result);
        Assert.Contains("TO_CHAR", result);
        Assert.Contains("MM/DD/YYYY", result);
    }

    [Fact]
    public void MapFunction_CONVERT_WithStyleCode120_ReturnsToChar()
    {
        var result = _mapper.MapFunction("CONVERT", ["VARCHAR(20)", "created_at", "120"]);

        Assert.NotNull(result);
        Assert.Contains("TO_CHAR", result);
        Assert.Contains("YYYY-MM-DD HH24:MI:SS", result);
    }

    [Fact]
    public void MapFunction_CONVERT_WithoutStyleCode_ReturnsCAST()
    {
        var result = _mapper.MapFunction("CONVERT", ["INTEGER", "col1"]);

        Assert.NotNull(result);
        Assert.Contains("CAST", result);
    }

    [Fact]
    public void MapFunction_UnmappedFunction_ReturnsNull()
    {
        var result = _mapper.MapFunction("TOTALLY_FAKE_FUNCTION", ["arg1"]);

        Assert.Null(result);
    }

    [Fact]
    public void MapFunction_IsCaseInsensitive()
    {
        var result = _mapper.MapFunction("getdate", []);

        Assert.Equal("CURRENT_TIMESTAMP", result);
    }

    [Fact]
    public void MapFunction_WrongArgCount_ReturnsNull()
    {
        // LEN expects 1 arg, passing 3
        var result = _mapper.MapFunction("LEN", ["a", "b", "c"]);

        Assert.Null(result);
    }

    [Fact]
    public void Constructor_ThrowsOnMissingConfigFile()
    {
        Assert.Throws<InvalidOperationException>(() =>
            new FunctionMapper("nonexistent.json", NullLogger<FunctionMapper>.Instance));
    }

    private static string FindConfigDirectory()
    {
        var dir = Directory.GetCurrentDirectory();
        while (dir is not null)
        {
            var configPath = Path.Combine(dir, "config");
            if (Directory.Exists(configPath) && File.Exists(Path.Combine(configPath, "function-mappings.json")))
            {
                return configPath;
            }
            dir = Directory.GetParent(dir)?.FullName;
        }

        throw new InvalidOperationException(
            "Could not find config directory.");
    }
}
