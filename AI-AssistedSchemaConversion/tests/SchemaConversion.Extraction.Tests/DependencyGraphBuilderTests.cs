using SchemaConversion.Core.Models;
using SchemaConversion.Extraction;
using Xunit;

namespace SchemaConversion.Extraction.Tests;

public class DependencyGraphBuilderTests
{
    private readonly DependencyGraphBuilder _builder = new();

    [Fact]
    public void GetProcessingOrder_EmptyList_ReturnsEmptyResult()
    {
        var result = _builder.GetProcessingOrder([]);

        Assert.Empty(result.Ordered);
        Assert.Empty(result.Cycles);
    }

    [Fact]
    public void GetProcessingOrder_SingleObject_ReturnsIt()
    {
        var objects = new List<SchemaObject>
        {
            CreateObject("dbo", "Customers")
        };

        var result = _builder.GetProcessingOrder(objects);

        Assert.Single(result.Ordered);
        Assert.Equal("Customers", result.Ordered[0].Name);
        Assert.Empty(result.Cycles);
    }

    [Fact]
    public void GetProcessingOrder_LinearChain_ReturnsDependencyFirst()
    {
        // A depends on B, B depends on C → C, B, A
        var objects = new List<SchemaObject>
        {
            CreateObject("dbo", "A", ["dbo.B"]),
            CreateObject("dbo", "B", ["dbo.C"]),
            CreateObject("dbo", "C")
        };

        var result = _builder.GetProcessingOrder(objects);

        Assert.Equal(3, result.Ordered.Count);
        Assert.Empty(result.Cycles);

        var indexC = result.Ordered.ToList().FindIndex(o => o.Name == "C");
        var indexB = result.Ordered.ToList().FindIndex(o => o.Name == "B");
        var indexA = result.Ordered.ToList().FindIndex(o => o.Name == "A");

        Assert.True(indexC < indexB, "C should come before B");
        Assert.True(indexB < indexA, "B should come before A");
    }

    [Fact]
    public void GetProcessingOrder_DiamondDependency_ResolvedCorrectly()
    {
        // D depends on B and C; B depends on A; C depends on A
        var objects = new List<SchemaObject>
        {
            CreateObject("dbo", "D", ["dbo.B", "dbo.C"]),
            CreateObject("dbo", "B", ["dbo.A"]),
            CreateObject("dbo", "C", ["dbo.A"]),
            CreateObject("dbo", "A")
        };

        var result = _builder.GetProcessingOrder(objects);

        Assert.Equal(4, result.Ordered.Count);
        Assert.Empty(result.Cycles);

        var indexA = result.Ordered.ToList().FindIndex(o => o.Name == "A");
        var indexB = result.Ordered.ToList().FindIndex(o => o.Name == "B");
        var indexC = result.Ordered.ToList().FindIndex(o => o.Name == "C");
        var indexD = result.Ordered.ToList().FindIndex(o => o.Name == "D");

        Assert.True(indexA < indexB, "A should come before B");
        Assert.True(indexA < indexC, "A should come before C");
        Assert.True(indexB < indexD, "B should come before D");
        Assert.True(indexC < indexD, "C should come before D");
    }

    [Fact]
    public void GetProcessingOrder_CycleDetection_ReportsCycle()
    {
        // A depends on B, B depends on A — a cycle
        var objects = new List<SchemaObject>
        {
            CreateObject("dbo", "A", ["dbo.B"]),
            CreateObject("dbo", "B", ["dbo.A"])
        };

        var result = _builder.GetProcessingOrder(objects);

        Assert.NotEmpty(result.Cycles);
        var cycleMembers = result.Cycles.SelectMany(c => c.Select(o => o.Name)).ToList();
        Assert.Contains("A", cycleMembers);
        Assert.Contains("B", cycleMembers);
    }

    [Fact]
    public void GetProcessingOrder_ThreeNodeCycle_DetectedCorrectly()
    {
        // A→B→C→A
        var objects = new List<SchemaObject>
        {
            CreateObject("dbo", "A", ["dbo.B"]),
            CreateObject("dbo", "B", ["dbo.C"]),
            CreateObject("dbo", "C", ["dbo.A"])
        };

        var result = _builder.GetProcessingOrder(objects);

        Assert.NotEmpty(result.Cycles);
        var cycleMembers = result.Cycles.SelectMany(c => c.Select(o => o.Name)).ToList();
        Assert.Contains("A", cycleMembers);
        Assert.Contains("B", cycleMembers);
        Assert.Contains("C", cycleMembers);
    }

    [Fact]
    public void GetProcessingOrder_CycleWithNonCycleNodes_SeparatesCorrectly()
    {
        // D depends on nothing; A→B→A (cycle)
        var objects = new List<SchemaObject>
        {
            CreateObject("dbo", "A", ["dbo.B"]),
            CreateObject("dbo", "B", ["dbo.A"]),
            CreateObject("dbo", "D")
        };

        var result = _builder.GetProcessingOrder(objects);

        Assert.NotEmpty(result.Cycles);
        // D should be in the ordered list (no cycle involvement)
        Assert.Contains(result.Ordered, o => o.Name == "D");
    }

    [Fact]
    public void GetProcessingOrder_ExternalDependency_IgnoredGracefully()
    {
        // A depends on "dbo.External" which is not in our object set
        var objects = new List<SchemaObject>
        {
            CreateObject("dbo", "A", ["dbo.External"]),
            CreateObject("dbo", "B")
        };

        var result = _builder.GetProcessingOrder(objects);

        Assert.Equal(2, result.Ordered.Count);
        Assert.Empty(result.Cycles);
    }

    [Fact]
    public void GetProcessingOrder_NoDependent_AllReturned()
    {
        var objects = new List<SchemaObject>
        {
            CreateObject("dbo", "Table1"),
            CreateObject("dbo", "Table2"),
            CreateObject("dbo", "Table3")
        };

        var result = _builder.GetProcessingOrder(objects);

        Assert.Equal(3, result.Ordered.Count);
        Assert.Empty(result.Cycles);
    }

    private static SchemaObject CreateObject(
        string schema, string name, IReadOnlyList<string>? dependsOn = null)
    {
        return new SchemaObject
        {
            SchemaName = schema,
            Name = name,
            ObjectType = SchemaObjectType.Table,
            SourceDefinition = $"CREATE TABLE [{schema}].[{name}] (Id INT)",
            SourceDefinitionHash = $"hash-{schema}-{name}",
            DependsOn = dependsOn ?? []
        };
    }
}
