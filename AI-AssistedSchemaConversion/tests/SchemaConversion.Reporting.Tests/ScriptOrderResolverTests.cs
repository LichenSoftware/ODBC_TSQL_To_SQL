using SchemaConversion.Core.Models;
using SchemaConversion.Reporting;

namespace SchemaConversion.Reporting.Tests;

public class ScriptOrderResolverTests
{
    private readonly ScriptOrderResolver _resolver;

    public ScriptOrderResolverTests()
    {
        _resolver = new ScriptOrderResolver();
    }

    [Fact]
    public void Resolve_EmptyEntries_ReturnsEmptyList()
    {
        var result = _resolver.Resolve([]);

        Assert.Empty(result);
    }

    [Fact]
    public void Resolve_SchemasComeBefore_Types()
    {
        var entries = new List<ConversionSessionEntry>
        {
            CreateConvertedEntry("dbo", "MyType", SchemaObjectType.UserDefinedType, "CREATE TYPE dbo.MyType AS (id INTEGER)"),
            CreateConvertedEntry("dbo", "dbo", SchemaObjectType.Schema, "CREATE SCHEMA IF NOT EXISTS dbo"),
        };

        var result = _resolver.Resolve(entries).ToList();

        var schemaIndex = result.FindIndex(e => e.Entry.Source.ObjectType == SchemaObjectType.Schema);
        var typeIndex = result.FindIndex(e => e.Entry.Source.ObjectType == SchemaObjectType.UserDefinedType);

        Assert.True(schemaIndex < typeIndex, "Schema should come before UserDefinedType");
    }

    [Fact]
    public void Resolve_TypesComeBefore_Tables()
    {
        var entries = new List<ConversionSessionEntry>
        {
            CreateConvertedEntry("dbo", "Customers", SchemaObjectType.Table, "CREATE TABLE dbo.Customers (Id INTEGER)"),
            CreateConvertedEntry("dbo", "EmailType", SchemaObjectType.UserDefinedType, "CREATE DOMAIN EmailType AS VARCHAR(255)"),
        };

        var result = _resolver.Resolve(entries).ToList();

        var typeIndex = result.FindIndex(e => e.Entry.Source.ObjectType == SchemaObjectType.UserDefinedType);
        var tableIndex = result.FindIndex(e => e.Entry.Source.ObjectType == SchemaObjectType.Table);

        Assert.True(typeIndex < tableIndex, "UserDefinedType should come before Table");
    }

    [Fact]
    public void Resolve_SequencesComeBefore_Tables()
    {
        var entries = new List<ConversionSessionEntry>
        {
            CreateConvertedEntry("dbo", "Customers", SchemaObjectType.Table, "CREATE TABLE dbo.Customers (Id INTEGER)"),
            CreateConvertedEntry("dbo", "CustomerSeq", SchemaObjectType.Sequence, "CREATE SEQUENCE dbo.CustomerSeq"),
        };

        var result = _resolver.Resolve(entries).ToList();

        var seqIndex = result.FindIndex(e => e.Entry.Source.ObjectType == SchemaObjectType.Sequence);
        var tableIndex = result.FindIndex(e => e.Entry.Source.ObjectType == SchemaObjectType.Table);

        Assert.True(seqIndex < tableIndex, "Sequence should come before Table");
    }

    [Fact]
    public void Resolve_TablesComeBefore_Indexes()
    {
        var entries = new List<ConversionSessionEntry>
        {
            CreateConvertedEntry("dbo", "IX_Customers_Name", SchemaObjectType.Index, "CREATE INDEX IX_Customers_Name ON dbo.Customers(Name)"),
            CreateConvertedEntry("dbo", "Customers", SchemaObjectType.Table, "CREATE TABLE dbo.Customers (Id INTEGER, Name VARCHAR(100))"),
        };

        var result = _resolver.Resolve(entries).ToList();

        var tableIndex = result.FindIndex(e => e.Entry.Source.ObjectType == SchemaObjectType.Table);
        var indexIndex = result.FindIndex(e => e.Entry.Source.ObjectType == SchemaObjectType.Index);

        Assert.True(tableIndex < indexIndex, "Table should come before Index");
    }

    [Fact]
    public void Resolve_FunctionsComeBefore_Triggers()
    {
        var entries = new List<ConversionSessionEntry>
        {
            CreateConvertedEntry("dbo", "TriggerOnInsert", SchemaObjectType.Trigger, "CREATE TRIGGER dbo.TriggerOnInsert"),
            CreateConvertedEntry("dbo", "TriggerFunc", SchemaObjectType.Function, "CREATE FUNCTION dbo.TriggerFunc()"),
        };

        var result = _resolver.Resolve(entries).ToList();

        var funcIndex = result.FindIndex(e => e.Entry.Source.ObjectType == SchemaObjectType.Function);
        var triggerIndex = result.FindIndex(e => e.Entry.Source.ObjectType == SchemaObjectType.Trigger);

        Assert.True(funcIndex < triggerIndex, "Function should come before Trigger");
    }

    [Fact]
    public void Resolve_TriggersComeBefore_Views()
    {
        var entries = new List<ConversionSessionEntry>
        {
            CreateConvertedEntry("dbo", "CustomerView", SchemaObjectType.View, "CREATE VIEW dbo.CustomerView AS SELECT * FROM dbo.Customers"),
            CreateConvertedEntry("dbo", "AuditTrigger", SchemaObjectType.Trigger, "CREATE TRIGGER dbo.AuditTrigger"),
        };

        var result = _resolver.Resolve(entries).ToList();

        var triggerIndex = result.FindIndex(e => e.Entry.Source.ObjectType == SchemaObjectType.Trigger);
        var viewIndex = result.FindIndex(e => e.Entry.Source.ObjectType == SchemaObjectType.View);

        Assert.True(triggerIndex < viewIndex, "Trigger should come before View");
    }

    [Fact]
    public void Resolve_ViewsComeBefore_Permissions()
    {
        var entries = new List<ConversionSessionEntry>
        {
            CreateConvertedEntry("dbo", "ReadPerms", SchemaObjectType.Permission, "GRANT SELECT ON dbo.Customers TO app_user"),
            CreateConvertedEntry("dbo", "CustomerView", SchemaObjectType.View, "CREATE VIEW dbo.CustomerView AS SELECT * FROM dbo.Customers"),
        };

        var result = _resolver.Resolve(entries).ToList();

        var viewIndex = result.FindIndex(e => e.Entry.Source.ObjectType == SchemaObjectType.View);
        var permIndex = result.FindIndex(e => e.Entry.Source.ObjectType == SchemaObjectType.Permission);

        Assert.True(viewIndex < permIndex, "View should come before Permission");
    }

    [Fact]
    public void Resolve_WrapperDdl_PlacedAfterViews_BeforePermissions()
    {
        var entries = new List<ConversionSessionEntry>
        {
            CreateConvertedEntry("dbo", "ReadPerms", SchemaObjectType.Permission, "GRANT SELECT ON dbo.Customers TO app_user"),
            CreateEntryWithWrapper("dbo", "GetOrders", SchemaObjectType.Function,
                "CREATE FUNCTION dbo.get_orders()", "CREATE FUNCTION dbo.GetOrders()"),
            CreateConvertedEntry("dbo", "CustomerView", SchemaObjectType.View, "CREATE VIEW dbo.CustomerView AS SELECT * FROM dbo.Customers"),
        };

        var result = _resolver.Resolve(entries).ToList();

        var viewIndex = result.FindIndex(e => e.Entry.Source.ObjectType == SchemaObjectType.View && !e.IsWrapper);
        var wrapperIndex = result.FindIndex(e => e.IsWrapper);
        var permIndex = result.FindIndex(e => e.Entry.Source.ObjectType == SchemaObjectType.Permission);

        Assert.True(viewIndex < wrapperIndex, "View should come before Wrapper");
        Assert.True(wrapperIndex < permIndex, "Wrapper should come before Permission");
    }

    [Fact]
    public void Resolve_ExcludesPendingAndFailedEntries()
    {
        var entries = new List<ConversionSessionEntry>
        {
            CreateConvertedEntry("dbo", "Table1", SchemaObjectType.Table, "CREATE TABLE dbo.Table1 (Id INTEGER)"),
            CreateEntry("dbo", "Table2", SchemaObjectType.Table, ConversionStatus.Pending, ""),
            CreateEntry("dbo", "Table3", SchemaObjectType.Table, ConversionStatus.Failed, ""),
        };

        var result = _resolver.Resolve(entries);

        Assert.Single(result);
        Assert.Equal("Table1", result[0].Entry.Source.Name);
    }

    [Fact]
    public void Resolve_ExcludesEntriesWithNullDdl()
    {
        var entries = new List<ConversionSessionEntry>
        {
            CreateEntry("dbo", "Table1", SchemaObjectType.Table, ConversionStatus.Converted, null),
            CreateConvertedEntry("dbo", "Table2", SchemaObjectType.Table, "CREATE TABLE dbo.Table2 (Id INTEGER)"),
        };

        var result = _resolver.Resolve(entries);

        Assert.Single(result);
        Assert.Equal("Table2", result[0].Entry.Source.Name);
    }

    [Fact]
    public void Resolve_FullCategoryOrder_MatchesDesignSpec()
    {
        var entries = new List<ConversionSessionEntry>
        {
            CreateConvertedEntry("dbo", "ReadPerms", SchemaObjectType.Permission, "GRANT SELECT"),
            CreateConvertedEntry("dbo", "CustomerView", SchemaObjectType.View, "CREATE VIEW"),
            CreateConvertedEntry("dbo", "AuditTrigger", SchemaObjectType.Trigger, "CREATE TRIGGER"),
            CreateConvertedEntry("dbo", "GetOrders", SchemaObjectType.Function, "CREATE FUNCTION"),
            CreateConvertedEntry("dbo", "IX_Name", SchemaObjectType.Index, "CREATE INDEX"),
            CreateConvertedEntry("dbo", "Customers", SchemaObjectType.Table, "CREATE TABLE"),
            CreateConvertedEntry("dbo", "CustomerSeq", SchemaObjectType.Sequence, "CREATE SEQUENCE"),
            CreateConvertedEntry("dbo", "EmailType", SchemaObjectType.UserDefinedType, "CREATE DOMAIN"),
            CreateConvertedEntry("dbo", "public", SchemaObjectType.Schema, "CREATE SCHEMA"),
        };

        var result = _resolver.Resolve(entries);

        Assert.Equal("public", result[0].Entry.Source.Name);        // Schema
        Assert.Equal("EmailType", result[1].Entry.Source.Name);     // Type
        Assert.Equal("CustomerSeq", result[2].Entry.Source.Name);   // Sequence
        Assert.Equal("Customers", result[3].Entry.Source.Name);     // Table
        Assert.Equal("IX_Name", result[4].Entry.Source.Name);       // Index
        Assert.Equal("GetOrders", result[5].Entry.Source.Name);     // Function
        Assert.Equal("AuditTrigger", result[6].Entry.Source.Name);  // Trigger
        Assert.Equal("CustomerView", result[7].Entry.Source.Name);  // View
        Assert.Equal("ReadPerms", result[8].Entry.Source.Name);     // Permission
    }

    [Fact]
    public void Resolve_StoredProcedures_InSameCategoryAsFunctions()
    {
        var entries = new List<ConversionSessionEntry>
        {
            CreateConvertedEntry("dbo", "GetData", SchemaObjectType.StoredProcedure, "CREATE PROCEDURE"),
            CreateConvertedEntry("dbo", "CalcTotal", SchemaObjectType.Function, "CREATE FUNCTION"),
            CreateConvertedEntry("dbo", "Customers", SchemaObjectType.Table, "CREATE TABLE"),
        };

        var result = _resolver.Resolve(entries).ToList();

        var tablePos = result.FindIndex(e => e.Entry.Source.Name == "Customers");
        var funcPos = result.FindIndex(e => e.Entry.Source.Name == "CalcTotal");
        var procPos = result.FindIndex(e => e.Entry.Source.Name == "GetData");

        Assert.True(tablePos < funcPos, "Table comes before Function");
        Assert.True(tablePos < procPos, "Table comes before StoredProcedure");
    }

    private static ConversionSessionEntry CreateConvertedEntry(
        string schema, string name, SchemaObjectType type, string ddl)
    {
        return new ConversionSessionEntry
        {
            Source = new SchemaObject
            {
                SchemaName = schema,
                Name = name,
                ObjectType = type,
                SourceDefinition = $"-- source for {schema}.{name}",
                SourceDefinitionHash = $"hash-{schema}-{name}"
            },
            Result = new ConversionResult
            {
                ObjectName = name,
                SchemaName = schema,
                ObjectType = type,
                Status = ConversionStatus.Converted,
                Method = ConversionMethod.RuleBased,
                GeneratedDdl = ddl
            },
            ConvertedAt = DateTimeOffset.UtcNow
        };
    }

    private static ConversionSessionEntry CreateEntry(
        string schema, string name, SchemaObjectType type,
        ConversionStatus status, string? ddl)
    {
        return new ConversionSessionEntry
        {
            Source = new SchemaObject
            {
                SchemaName = schema,
                Name = name,
                ObjectType = type,
                SourceDefinition = $"-- source for {schema}.{name}",
                SourceDefinitionHash = $"hash-{schema}-{name}"
            },
            Result = new ConversionResult
            {
                ObjectName = name,
                SchemaName = schema,
                ObjectType = type,
                Status = status,
                Method = ConversionMethod.RuleBased,
                GeneratedDdl = ddl
            },
            ConvertedAt = DateTimeOffset.UtcNow
        };
    }

    private static ConversionSessionEntry CreateEntryWithWrapper(
        string schema, string name, SchemaObjectType type,
        string ddl, string wrapperDdl)
    {
        return new ConversionSessionEntry
        {
            Source = new SchemaObject
            {
                SchemaName = schema,
                Name = name,
                ObjectType = type,
                SourceDefinition = $"-- source for {schema}.{name}",
                SourceDefinitionHash = $"hash-{schema}-{name}"
            },
            Result = new ConversionResult
            {
                ObjectName = name,
                SchemaName = schema,
                ObjectType = type,
                Status = ConversionStatus.Converted,
                Method = ConversionMethod.AiAssisted,
                GeneratedDdl = ddl,
                WrapperDdl = wrapperDdl
            },
            ConvertedAt = DateTimeOffset.UtcNow
        };
    }
}
