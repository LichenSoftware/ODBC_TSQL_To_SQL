using Microsoft.Extensions.Logging.Abstractions;
using SchemaConversion.Core.Models;
using SchemaConversion.Orchestration;

namespace SchemaConversion.Orchestration.Tests;

public class ObjectClassifierTests
{
    private readonly ObjectClassifier _classifier;

    public ObjectClassifierTests()
    {
        _classifier = new ObjectClassifier(
            NullLogger<ObjectClassifier>.Instance);
    }

    [Theory]
    [InlineData(SchemaObjectType.Table)]
    [InlineData(SchemaObjectType.Constraint)]
    [InlineData(SchemaObjectType.Index)]
    [InlineData(SchemaObjectType.Sequence)]
    [InlineData(SchemaObjectType.UserDefinedType)]
    [InlineData(SchemaObjectType.Synonym)]
    [InlineData(SchemaObjectType.Schema)]
    [InlineData(SchemaObjectType.Permission)]
    public void Classify_RuleBasedObjectTypes_ReturnsRuleBased(SchemaObjectType objectType)
    {
        var obj = CreateSchemaObject("dbo", "TestObject", objectType, "CREATE TABLE dbo.TestObject (Id INT)");

        var result = _classifier.Classify(obj);

        Assert.Equal(ConversionMethod.RuleBased, result.Method);
    }

    [Theory]
    [InlineData(SchemaObjectType.StoredProcedure)]
    [InlineData(SchemaObjectType.Function)]
    [InlineData(SchemaObjectType.Trigger)]
    public void Classify_AiAssistedObjectTypes_ReturnsAiAssisted(SchemaObjectType objectType)
    {
        var obj = CreateSchemaObject("dbo", "TestObject", objectType, "CREATE PROCEDURE dbo.TestObject AS BEGIN END");

        var result = _classifier.Classify(obj);

        Assert.Equal(ConversionMethod.AiAssisted, result.Method);
    }

    [Fact]
    public void Classify_SimpleView_ReturnsRuleBased()
    {
        var obj = CreateSchemaObject("dbo", "SimpleView", SchemaObjectType.View,
            "CREATE VIEW dbo.SimpleView AS SELECT Id, Name FROM dbo.Customers");

        var result = _classifier.Classify(obj);

        Assert.Equal(ConversionMethod.RuleBased, result.Method);
    }

    [Theory]
    [InlineData("SELECT * FROM dbo.Orders CROSS APPLY dbo.GetItems(OrderId)")]
    [InlineData("SELECT * FROM dbo.Orders OUTER APPLY dbo.GetItems(OrderId)")]
    [InlineData("SELECT * FROM dbo.Data FOR XML PATH")]
    [InlineData("SELECT * FROM OPENROWSET('SQLOLEDB', 'server', 'SELECT 1')")]
    [InlineData("SELECT * FROM OPENJSON(@json)")]
    [InlineData("SELECT * FROM dbo.Sales PIVOT (SUM(Amount) FOR Month IN ([Jan],[Feb]))")]
    [InlineData("SELECT * FROM dbo.Sales UNPIVOT (Amount FOR Month IN (Jan, Feb))")]
    public void Classify_ViewWithSqlServerKeywords_ReturnsAiAssisted(string viewBody)
    {
        var obj = CreateSchemaObject("dbo", "ComplexView", SchemaObjectType.View,
            $"CREATE VIEW dbo.ComplexView AS {viewBody}");

        var result = _classifier.Classify(obj);

        Assert.Equal(ConversionMethod.AiAssisted, result.Method);
        Assert.Contains("SQL Server-specific keyword", result.Reason);
    }

    [Fact]
    public void Classify_ForceAiOverride_ReturnsAiAssisted()
    {
        var classifier = new ObjectClassifier(
            NullLogger<ObjectClassifier>.Instance,
            forceAiObjects: ["dbo.TestTable"]);

        var obj = CreateSchemaObject("dbo", "TestTable", SchemaObjectType.Table,
            "CREATE TABLE dbo.TestTable (Id INT)");

        var result = classifier.Classify(obj);

        Assert.Equal(ConversionMethod.AiAssisted, result.Method);
        Assert.Contains("Manual override", result.Reason);
    }

    [Fact]
    public void Classify_ForceRulesOverride_ReturnsRuleBased()
    {
        var classifier = new ObjectClassifier(
            NullLogger<ObjectClassifier>.Instance,
            forceRulesObjects: ["dbo.ComplexProc"]);

        var obj = CreateSchemaObject("dbo", "ComplexProc", SchemaObjectType.StoredProcedure,
            "CREATE PROCEDURE dbo.ComplexProc AS BEGIN END");

        var result = classifier.Classify(obj);

        Assert.Equal(ConversionMethod.RuleBased, result.Method);
        Assert.Contains("Manual override", result.Reason);
    }

    [Fact]
    public void Classify_ForceRulesTakesPriorityOverForceAi()
    {
        var classifier = new ObjectClassifier(
            NullLogger<ObjectClassifier>.Instance,
            forceAiObjects: ["dbo.TestObject"],
            forceRulesObjects: ["dbo.TestObject"]);

        var obj = CreateSchemaObject("dbo", "TestObject", SchemaObjectType.Table,
            "CREATE TABLE dbo.TestObject (Id INT)");

        var result = classifier.Classify(obj);

        Assert.Equal(ConversionMethod.RuleBased, result.Method);
    }

    [Fact]
    public void Classify_ForceOverrideMatchesByNameOnly()
    {
        var classifier = new ObjectClassifier(
            NullLogger<ObjectClassifier>.Instance,
            forceAiObjects: ["MyTable"]);

        var obj = CreateSchemaObject("dbo", "MyTable", SchemaObjectType.Table,
            "CREATE TABLE dbo.MyTable (Id INT)");

        var result = classifier.Classify(obj);

        Assert.Equal(ConversionMethod.AiAssisted, result.Method);
    }

    [Fact]
    public void Classify_ViewWithEmptySourceDefinition_ReturnsRuleBased()
    {
        var obj = CreateSchemaObject("dbo", "EmptyView", SchemaObjectType.View, "");

        var result = _classifier.Classify(obj);

        Assert.Equal(ConversionMethod.RuleBased, result.Method);
    }

    private static SchemaObject CreateSchemaObject(
        string schema, string name, SchemaObjectType type, string source)
    {
        return new SchemaObject
        {
            SchemaName = schema,
            Name = name,
            ObjectType = type,
            SourceDefinition = source,
            SourceDefinitionHash = Convert.ToHexString(
                System.Security.Cryptography.SHA256.HashData(
                    System.Text.Encoding.UTF8.GetBytes(source)))
        };
    }
}
