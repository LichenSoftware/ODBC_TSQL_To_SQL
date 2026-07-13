using Microsoft.Extensions.Logging.Abstractions;
using SchemaConversion.Core.Models;
using SchemaConversion.Core.Options;
using Xunit;

namespace SchemaConversion.RuleEngine.Tests;

public class ConstraintConverterTests
{
    private readonly ConstraintConverter _converter;
    private readonly ConversionContext _context;

    public ConstraintConverterTests()
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

        _converter = new ConstraintConverter(
            expressionTranslator, NullLogger<ConstraintConverter>.Instance);

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
    public void Convert_PrimaryKeyConstraint_PreservesColumnsAndName()
    {
        var obj = CreateSchemaObject("PK_Orders", "dbo", @"
            ALTER TABLE dbo.Orders
            ADD CONSTRAINT PK_Orders PRIMARY KEY (OrderID ASC);");

        var result = _converter.Convert(obj, _context);

        Assert.Equal(ConversionStatus.Converted, result.Status);
        Assert.NotNull(result.GeneratedDdl);
        Assert.Contains("PRIMARY KEY", result.GeneratedDdl);
        Assert.Contains("pk_orders", result.GeneratedDdl.ToLowerInvariant());
        Assert.Contains("orderid", result.GeneratedDdl.ToLowerInvariant());
    }

    [Fact]
    public void Convert_CompositePrimaryKey_PreservesAllColumns()
    {
        var obj = CreateSchemaObject("PK_OrderItems", "dbo", @"
            ALTER TABLE dbo.OrderItems
            ADD CONSTRAINT PK_OrderItems PRIMARY KEY (OrderID ASC, ItemID DESC);");

        var result = _converter.Convert(obj, _context);

        Assert.Equal(ConversionStatus.Converted, result.Status);
        Assert.NotNull(result.GeneratedDdl);
        Assert.Contains("orderid", result.GeneratedDdl.ToLowerInvariant());
        Assert.Contains("itemid DESC", result.GeneratedDdl, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Convert_ForeignKeyConstraint_PreservesReferences()
    {
        var obj = CreateSchemaObject("FK_Orders_Customers", "dbo", @"
            ALTER TABLE dbo.Orders
            ADD CONSTRAINT FK_Orders_Customers 
            FOREIGN KEY (CustomerID) REFERENCES dbo.Customers (CustomerID)
            ON DELETE CASCADE ON UPDATE NO ACTION;");

        var result = _converter.Convert(obj, _context);

        Assert.Equal(ConversionStatus.Converted, result.Status);
        Assert.NotNull(result.GeneratedDdl);
        Assert.Contains("FOREIGN KEY", result.GeneratedDdl);
        Assert.Contains("REFERENCES", result.GeneratedDdl);
        Assert.Contains("public", result.GeneratedDdl);
        Assert.Contains("customers", result.GeneratedDdl);
        Assert.Contains("ON DELETE CASCADE", result.GeneratedDdl);
        Assert.Contains("ON UPDATE NO ACTION", result.GeneratedDdl);
    }

    [Fact]
    public void Convert_ForeignKeyNoAction_DefaultsToNoAction()
    {
        var obj = CreateSchemaObject("FK_Orders_Products", "dbo", @"
            ALTER TABLE dbo.Orders
            ADD CONSTRAINT FK_Orders_Products 
            FOREIGN KEY (ProductID) REFERENCES dbo.Products (ProductID);");

        var result = _converter.Convert(obj, _context);

        Assert.Equal(ConversionStatus.Converted, result.Status);
        Assert.NotNull(result.GeneratedDdl);
        Assert.Contains("ON DELETE NO ACTION", result.GeneratedDdl);
        Assert.Contains("ON UPDATE NO ACTION", result.GeneratedDdl);
    }

    [Fact]
    public void Convert_ForeignKeyWithSchemaMapping_AppliesMapping()
    {
        var obj = CreateSchemaObject("FK_Items_Products", "sales", @"
            ALTER TABLE sales.Items
            ADD CONSTRAINT FK_Items_Products 
            FOREIGN KEY (ProductID) REFERENCES dbo.Products (ProductID);");

        var result = _converter.Convert(obj, _context);

        Assert.Equal(ConversionStatus.Converted, result.Status);
        Assert.NotNull(result.GeneratedDdl);
        Assert.Contains("REFERENCES", result.GeneratedDdl);
        Assert.Contains("public", result.GeneratedDdl);
        Assert.Contains("products", result.GeneratedDdl);
    }

    [Fact]
    public void Convert_UniqueConstraint_PreservesColumnsAndName()
    {
        var obj = CreateSchemaObject("UQ_Customers_Email", "dbo", @"
            ALTER TABLE dbo.Customers
            ADD CONSTRAINT UQ_Customers_Email UNIQUE (Email);");

        var result = _converter.Convert(obj, _context);

        Assert.Equal(ConversionStatus.Converted, result.Status);
        Assert.NotNull(result.GeneratedDdl);
        Assert.Contains("UNIQUE", result.GeneratedDdl);
        Assert.Contains("uq_customers_email", result.GeneratedDdl.ToLowerInvariant());
        Assert.Contains("email", result.GeneratedDdl.ToLowerInvariant());
    }

    [Fact]
    public void Convert_CheckConstraint_TranslatesExpression()
    {
        var obj = CreateSchemaObject("CK_Orders_Amount", "dbo", @"
            ALTER TABLE dbo.Orders
            ADD CONSTRAINT CK_Orders_Amount CHECK (Amount > 0);");

        var result = _converter.Convert(obj, _context);

        Assert.Equal(ConversionStatus.Converted, result.Status);
        Assert.NotNull(result.GeneratedDdl);
        Assert.Contains("CHECK", result.GeneratedDdl);
        Assert.Contains("ck_orders_amount", result.GeneratedDdl.ToLowerInvariant());
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
            ObjectType = SchemaObjectType.Constraint,
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
