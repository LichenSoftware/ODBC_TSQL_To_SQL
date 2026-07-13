using Microsoft.Extensions.Logging.Abstractions;
using SchemaConversion.Core.Models;
using SchemaConversion.Orchestration;

namespace SchemaConversion.Orchestration.Tests;

public sealed class SessionChangeDetectorTests
{
    private readonly SessionChangeDetector _detector = new(NullLogger<SessionChangeDetector>.Instance);

    [Fact]
    public void GetObjectsRequiringProcessing_ReturnsNewObjects()
    {
        var current = new List<SchemaObject>
        {
            CreateObject("dbo", "Customers", "hash1"),
            CreateObject("dbo", "Orders", "hash2")
        };

        IReadOnlyList<ConversionSessionEntry> existing = [];

        var result = _detector.GetObjectsRequiringProcessing(current, existing, null);

        Assert.Equal(2, result.Count);
    }

    [Fact]
    public void GetObjectsRequiringProcessing_ReturnsModifiedObjects()
    {
        var current = new List<SchemaObject>
        {
            CreateObject("dbo", "Customers", "newHash")
        };

        var existing = new List<ConversionSessionEntry>
        {
            CreateEntry("dbo", "Customers", "oldHash", ConversionStatus.Converted)
        };

        var result = _detector.GetObjectsRequiringProcessing(current, existing, null);

        Assert.Single(result);
        Assert.Equal("Customers", result[0].Name);
    }

    [Fact]
    public void GetObjectsRequiringProcessing_SkipsUnchangedObjects()
    {
        var current = new List<SchemaObject>
        {
            CreateObject("dbo", "Customers", "sameHash")
        };

        var existing = new List<ConversionSessionEntry>
        {
            CreateEntry("dbo", "Customers", "sameHash", ConversionStatus.Converted)
        };

        var result = _detector.GetObjectsRequiringProcessing(current, existing, null);

        Assert.Empty(result);
    }

    [Fact]
    public void GetObjectsRequiringProcessing_ExcludesManuallyReviewed()
    {
        var current = new List<SchemaObject>
        {
            CreateObject("dbo", "Customers", "newHash")
        };

        var existing = new List<ConversionSessionEntry>
        {
            CreateEntry("dbo", "Customers", "oldHash", ConversionStatus.ManuallyReviewed)
        };

        var result = _detector.GetObjectsRequiringProcessing(current, existing, null);

        Assert.Empty(result);
    }

    [Fact]
    public void GetObjectsRequiringProcessing_RespectsSchemaFilter()
    {
        var current = new List<SchemaObject>
        {
            CreateObject("dbo", "Customers", "hash1"),
            CreateObject("sales", "Orders", "hash2"),
            CreateObject("hr", "Employees", "hash3")
        };

        IReadOnlyList<ConversionSessionEntry> existing = [];

        var filters = new ConversionFilters
        {
            Schemas = new List<string> { "dbo", "sales" }
        };

        var result = _detector.GetObjectsRequiringProcessing(current, existing, filters);

        Assert.Equal(2, result.Count);
        Assert.DoesNotContain(result, o => o.SchemaName == "hr");
    }

    [Fact]
    public void GetObjectsRequiringProcessing_RespectsTypeFilter()
    {
        var current = new List<SchemaObject>
        {
            CreateObject("dbo", "Customers", "hash1", SchemaObjectType.Table),
            CreateObject("dbo", "GetOrders", "hash2", SchemaObjectType.StoredProcedure),
            CreateObject("dbo", "OrderView", "hash3", SchemaObjectType.View)
        };

        IReadOnlyList<ConversionSessionEntry> existing = [];

        var filters = new ConversionFilters
        {
            Types = new List<SchemaObjectType> { SchemaObjectType.Table, SchemaObjectType.View }
        };

        var result = _detector.GetObjectsRequiringProcessing(current, existing, filters);

        Assert.Equal(2, result.Count);
        Assert.DoesNotContain(result, o => o.ObjectType == SchemaObjectType.StoredProcedure);
    }

    [Fact]
    public void GetObjectsRequiringProcessing_RespectsExplicitObjectFilter()
    {
        var current = new List<SchemaObject>
        {
            CreateObject("dbo", "Customers", "hash1"),
            CreateObject("dbo", "Orders", "hash2"),
            CreateObject("dbo", "Products", "hash3")
        };

        IReadOnlyList<ConversionSessionEntry> existing = [];

        var filters = new ConversionFilters
        {
            Objects = new List<string> { "dbo.Customers", "dbo.Orders" }
        };

        var result = _detector.GetObjectsRequiringProcessing(current, existing, filters);

        Assert.Equal(2, result.Count);
        Assert.DoesNotContain(result, o => o.Name == "Products");
    }

    private static SchemaObject CreateObject(
        string schema, string name, string hash,
        SchemaObjectType type = SchemaObjectType.Table)
    {
        return new SchemaObject
        {
            Name = name,
            SchemaName = schema,
            ObjectType = type,
            SourceDefinition = $"CREATE TABLE [{schema}].[{name}] ...",
            SourceDefinitionHash = hash,
            DependsOn = []
        };
    }

    private static ConversionSessionEntry CreateEntry(
        string schema, string name, string hash, ConversionStatus status)
    {
        return new ConversionSessionEntry
        {
            Source = new SchemaObject
            {
                Name = name,
                SchemaName = schema,
                ObjectType = SchemaObjectType.Table,
                SourceDefinition = "CREATE TABLE ...",
                SourceDefinitionHash = hash,
                DependsOn = []
            },
            Result = new ConversionResult
            {
                ObjectName = name,
                SchemaName = schema,
                ObjectType = SchemaObjectType.Table,
                Status = status,
                Method = ConversionMethod.RuleBased
            },
            ConvertedAt = DateTimeOffset.UtcNow,
            IsManuallyEdited = false
        };
    }
}
