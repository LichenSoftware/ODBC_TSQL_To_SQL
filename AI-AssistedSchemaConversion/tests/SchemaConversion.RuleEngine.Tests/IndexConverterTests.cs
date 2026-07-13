using Microsoft.Extensions.Logging.Abstractions;
using SchemaConversion.Core.Models;
using SchemaConversion.Core.Options;
using Xunit;

namespace SchemaConversion.RuleEngine.Tests;

public class IndexConverterTests
{
    private readonly IndexConverter _converter;
    private readonly ConversionContext _context;

    public IndexConverterTests()
    {
        var configDir = FindConfigDirectory();
        var typeMapper = new TypeMapper(
            Path.Combine(configDir, "type-mappings.json"),
            NullLogger<TypeMapper>.Instance);
        var functionMapper = new FunctionMapper(
            Path.Combine(configDir, "function-mappings.json"),
            NullLogger<FunctionMapper>.Instance);
        var expressionTranslator = new ExpressionTranslator(
            typeMapper, functionMapper, NullLogger<ExpressionTranslator>.Instance);

        _converter = new IndexConverter(
            expressionTranslator, NullLogger<IndexConverter>.Instance);

        _context = new ConversionContext
        {
            SessionId = "test-session",
            SchemaMappings = new Dictionary<string, string>
            {
                { "dbo", "public" },
                { "sales", "sales" }
            }
        };
    }

    [Fact]
    public void Convert_StandardIndex_ProducesCreateIndex()
    {
        var obj = CreateSchemaObject("IX_Orders_CustomerID", "dbo", @"
            CREATE INDEX IX_Orders_CustomerID ON dbo.Orders (CustomerID ASC);");

        var result = _converter.Convert(obj, _context);

        Assert.Equal(ConversionStatus.Converted, result.Status);
        Assert.NotNull(result.GeneratedDdl);
        Assert.Contains("CREATE INDEX", result.GeneratedDdl);
        Assert.Contains("ix_orders_customerid", result.GeneratedDdl.ToLowerInvariant());
        Assert.Contains("public.orders", result.GeneratedDdl.ToLowerInvariant());
        Assert.Contains("customerid", result.GeneratedDdl.ToLowerInvariant());
    }

    [Fact]
    public void Convert_UniqueIndex_ProducesCreateUniqueIndex()
    {
        var obj = CreateSchemaObject("UX_Customers_Email", "dbo", @"
            CREATE UNIQUE INDEX UX_Customers_Email ON dbo.Customers (Email);");

        var result = _converter.Convert(obj, _context);

        Assert.Equal(ConversionStatus.Converted, result.Status);
        Assert.NotNull(result.GeneratedDdl);
        Assert.Contains("CREATE UNIQUE INDEX", result.GeneratedDdl);
        Assert.Contains("ux_customers_email", result.GeneratedDdl.ToLowerInvariant());
    }

    [Fact]
    public void Convert_IndexWithIncludeColumns_ProducesIncludeClause()
    {
        var obj = CreateSchemaObject("IX_Orders_Date", "dbo", @"
            CREATE INDEX IX_Orders_Date ON dbo.Orders (OrderDate)
            INCLUDE (CustomerID, TotalAmount);");

        var result = _converter.Convert(obj, _context);

        Assert.Equal(ConversionStatus.Converted, result.Status);
        Assert.NotNull(result.GeneratedDdl);
        Assert.Contains("INCLUDE", result.GeneratedDdl);
        Assert.Contains("customerid", result.GeneratedDdl.ToLowerInvariant());
        Assert.Contains("totalamount", result.GeneratedDdl.ToLowerInvariant());
    }

    [Fact]
    public void Convert_FilteredIndex_ProducesPartialIndex()
    {
        var obj = CreateSchemaObject("IX_Orders_Active", "dbo", @"
            CREATE INDEX IX_Orders_Active ON dbo.Orders (OrderDate)
            WHERE IsActive = 1;");

        var result = _converter.Convert(obj, _context);

        Assert.Equal(ConversionStatus.Converted, result.Status);
        Assert.NotNull(result.GeneratedDdl);
        Assert.Contains("WHERE", result.GeneratedDdl);
    }

    [Fact]
    public void Convert_ClusteredIndex_AddsCompatibilityNote()
    {
        var obj = CreateSchemaObject("CIX_Orders_OrderDate", "dbo", @"
            CREATE CLUSTERED INDEX CIX_Orders_OrderDate ON dbo.Orders (OrderDate ASC);");

        var result = _converter.Convert(obj, _context);

        Assert.Equal(ConversionStatus.Converted, result.Status);
        Assert.NotNull(result.GeneratedDdl);
        Assert.Contains("CREATE INDEX", result.GeneratedDdl);
        Assert.NotEmpty(result.CompatibilityNotes);
        Assert.Contains(result.CompatibilityNotes,
            n => n.Category == "Clustering");
    }

    [Fact]
    public void Convert_MultiColumnIndex_PreservesSortOrder()
    {
        var obj = CreateSchemaObject("IX_Orders_Multi", "dbo", @"
            CREATE INDEX IX_Orders_Multi ON dbo.Orders (CustomerID ASC, OrderDate DESC);");

        var result = _converter.Convert(obj, _context);

        Assert.Equal(ConversionStatus.Converted, result.Status);
        Assert.NotNull(result.GeneratedDdl);
        Assert.Contains("customerid", result.GeneratedDdl.ToLowerInvariant());
        Assert.Contains("DESC", result.GeneratedDdl);
    }

    [Fact]
    public void Convert_IndexWithSchemaMapping_AppliesMapping()
    {
        var obj = CreateSchemaObject("IX_Items_Name", "sales", @"
            CREATE INDEX IX_Items_Name ON sales.Items (ItemName);");

        var result = _converter.Convert(obj, _context);

        Assert.Equal(ConversionStatus.Converted, result.Status);
        Assert.NotNull(result.GeneratedDdl);
        Assert.Contains("sales.items", result.GeneratedDdl.ToLowerInvariant());
    }

    [Fact]
    public void Convert_InvalidSql_ReturnsFailedStatus()
    {
        var obj = CreateSchemaObject("Bad", "dbo", "INVALID SQL");

        var result = _converter.Convert(obj, _context);

        Assert.Equal(ConversionStatus.Failed, result.Status);
    }

    private static SchemaObject CreateSchemaObject(string name, string schema, string ddl)
    {
        return new SchemaObject
        {
            Name = name,
            SchemaName = schema,
            ObjectType = SchemaObjectType.Index,
            SourceDefinition = ddl,
            SourceDefinitionHash = Convert.ToBase64String(
                System.Security.Cryptography.SHA256.HashData(
                    System.Text.Encoding.UTF8.GetBytes(ddl)))
        };
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
            "Could not find config directory. Ensure type-mappings.json and function-mappings.json exist in a config/ folder.");
    }
}
