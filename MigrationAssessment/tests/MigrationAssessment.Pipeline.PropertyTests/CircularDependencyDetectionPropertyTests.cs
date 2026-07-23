using FluentAssertions;
using FsCheck;
using FsCheck.Xunit;

namespace MigrationAssessment.Pipeline.PropertyTests;

/// <summary>
/// Property-based tests for Circular Dependency Detection.
/// Feature: migration-validation-pipeline, Property 17: Circular Dependency Detection
///
/// Validates: Requirements 6.6
///
/// For any set of DDL objects containing a circular dependency cycle, all objects participating
/// in the cycle SHALL be marked as "fail-syntax" with an error message indicating circular
/// dependency, and all objects not in the cycle SHALL be validated normally.
/// </summary>
public class CircularDependencyDetectionPropertyTests
{
    #region Property 17: Circular Dependency Detection

    /// <summary>
    /// Property 17: Circular Dependency Detection — all objects participating in a cycle
    /// are marked as "fail-syntax" with an error message indicating circular dependency.
    ///
    /// **Validates: Requirements 6.6**
    /// </summary>
    [Property(MaxTest = 200, Arbitrary = new[] { typeof(DependencyGraphArbitrary) })]
    public void Cycle_members_are_marked_fail_syntax_with_circular_dependency_message(DependencyGraphScenario scenario)
    {
        if (scenario == null || scenario.DdlStatements.Count == 0) return;

        var results = PgValidationEngine.ValidateWithDependencyResolution(scenario.DdlStatements);

        // All objects that are known to be in a cycle must be fail-syntax with circular dependency message
        foreach (var cycleMember in scenario.ExpectedCycleMembers)
        {
            var result = results.FirstOrDefault(r => r.ObjectName == cycleMember);
            result.Should().NotBeNull(
                because: $"cycle member '{cycleMember}' should have a validation result");

            result!.Status.Should().Be("fail-syntax",
                because: $"cycle member '{cycleMember}' must be marked as fail-syntax");

            result.ErrorMessage.Should().NotBeNullOrEmpty(
                because: $"cycle member '{cycleMember}' must have an error message");

            result.ErrorMessage!.ToLowerInvariant().Should().Contain("circular dependency",
                because: $"cycle member '{cycleMember}' error message must indicate circular dependency");
        }
    }

    /// <summary>
    /// Property 17: Circular Dependency Detection — objects NOT in any cycle are validated
    /// normally (they receive an independent pass/fail result based on their DDL validity,
    /// not a circular dependency error).
    ///
    /// **Validates: Requirements 6.6**
    /// </summary>
    [Property(MaxTest = 200, Arbitrary = new[] { typeof(DependencyGraphArbitrary) })]
    public void Non_cycle_objects_are_validated_normally(DependencyGraphScenario scenario)
    {
        if (scenario == null || scenario.DdlStatements.Count == 0) return;

        var results = PgValidationEngine.ValidateWithDependencyResolution(scenario.DdlStatements);

        // Objects not in a cycle should NOT have circular dependency error
        var nonCycleObjects = scenario.DdlStatements
            .Where(s => !scenario.ExpectedCycleMembers.Contains(s.ObjectName))
            .Select(s => s.ObjectName)
            .ToList();

        foreach (var objectName in nonCycleObjects)
        {
            var result = results.FirstOrDefault(r => r.ObjectName == objectName);
            result.Should().NotBeNull(
                because: $"non-cycle object '{objectName}' should have a validation result");

            // Non-cycle objects should not have "circular dependency" in their error message
            if (result!.ErrorMessage != null)
            {
                result.ErrorMessage.ToLowerInvariant().Should().NotContain("circular dependency",
                    because: $"non-cycle object '{objectName}' should be validated normally, not marked as circular dependency");
            }
        }
    }

    /// <summary>
    /// Property 17: Circular Dependency Detection — every DDL object in the input receives
    /// exactly one validation result (no objects are lost or duplicated).
    ///
    /// **Validates: Requirements 6.6**
    /// </summary>
    [Property(MaxTest = 200, Arbitrary = new[] { typeof(DependencyGraphArbitrary) })]
    public void Every_object_receives_exactly_one_validation_result(DependencyGraphScenario scenario)
    {
        if (scenario == null || scenario.DdlStatements.Count == 0) return;

        var results = PgValidationEngine.ValidateWithDependencyResolution(scenario.DdlStatements);

        // Every input object must have a result
        var inputNames = scenario.DdlStatements.Select(s => s.ObjectName).ToHashSet();
        var resultNames = results.Select(r => r.ObjectName).ToList();

        resultNames.Should().HaveCount(inputNames.Count,
            because: "every input object must receive exactly one validation result");

        foreach (var name in inputNames)
        {
            resultNames.Should().Contain(name,
                because: $"object '{name}' must have a validation result");
        }

        // No duplicates
        resultNames.Distinct().Count().Should().Be(resultNames.Count,
            because: "no object should have duplicate validation results");
    }

    /// <summary>
    /// Property 17: Circular Dependency Detection — DAGs (directed acyclic graphs) with no
    /// cycles should have zero objects marked as circular dependency failures.
    ///
    /// **Validates: Requirements 6.6**
    /// </summary>
    [Property(MaxTest = 200, Arbitrary = new[] { typeof(DagOnlyArbitrary) })]
    public void DAG_graphs_have_no_circular_dependency_failures(DependencyGraphScenario scenario)
    {
        if (scenario == null || scenario.DdlStatements.Count == 0) return;

        var results = PgValidationEngine.ValidateWithDependencyResolution(scenario.DdlStatements);

        // No object should be marked with circular dependency
        foreach (var result in results)
        {
            if (result.ErrorMessage != null)
            {
                result.ErrorMessage.ToLowerInvariant().Should().NotContain("circular dependency",
                    because: $"DAG object '{result.ObjectName}' should never be marked as circular dependency");
            }
        }
    }

    #endregion
}

#region Models for PG Validation

/// <summary>
/// Represents a DDL statement with its dependencies for validation.
/// </summary>
public record DdlStatement(
    string ObjectName,
    string ObjectType,
    string Ddl,
    List<string> Dependencies
);

/// <summary>
/// Represents a validation result from the PG validator.
/// </summary>
public record ValidationResult(
    string ObjectName,
    string Status,
    string? ErrorMessage,
    int? ErrorLineNumber,
    string ValidationMode
);

/// <summary>
/// A test scenario containing DDL statements and the expected cycle members.
/// </summary>
public class DependencyGraphScenario
{
    public List<DdlStatement> DdlStatements { get; set; } = new();
    public HashSet<string> ExpectedCycleMembers { get; set; } = new();

    public override string ToString()
    {
        var cycleInfo = ExpectedCycleMembers.Count > 0
            ? $"Cycles: [{string.Join(", ", ExpectedCycleMembers)}]"
            : "No cycles";
        return $"DependencyGraph({DdlStatements.Count} objects, {cycleInfo})";
    }
}

#endregion

#region C# Implementation of Circular Dependency Detection (DFS-based)

/// <summary>
/// C# implementation of the PostgreSQL validation engine's cycle detection logic,
/// matching the DFS-based approach in Invoke-PgValidation.ps1.
/// Used for property-based testing of the circular dependency detection.
/// </summary>
public static class PgValidationEngine
{
    /// <summary>
    /// Validates DDL statements with dependency resolution, detecting circular dependencies.
    /// Objects in cycles are marked fail-syntax with circular dependency error.
    /// Objects not in cycles are validated normally (syntax-only fallback).
    /// </summary>
    public static List<ValidationResult> ValidateWithDependencyResolution(List<DdlStatement> ddlStatements)
    {
        // Build dependency graph
        var graph = BuildDependencyGraph(ddlStatements);

        // Detect cycles using DFS
        var cycleMembers = FindCircularDependencies(graph);

        // Get topological order for non-cycle objects
        var sortedObjects = GetTopologicalSort(graph, cycleMembers);

        var results = new List<ValidationResult>();

        // Mark cycle members as fail-syntax
        foreach (var cycleMember in cycleMembers)
        {
            results.Add(new ValidationResult(
                ObjectName: cycleMember,
                Status: "fail-syntax",
                ErrorMessage: "Circular dependency detected: object participates in a dependency cycle",
                ErrorLineNumber: null,
                ValidationMode: "syntax-only"
            ));
        }

        // Validate non-cycle objects using syntax-only validation
        foreach (var objectName in sortedObjects)
        {
            var stmt = ddlStatements.FirstOrDefault(s => s.ObjectName == objectName);
            if (stmt == null) continue;

            var result = ValidateSyntaxOnly(stmt);
            results.Add(result);
        }

        return results;
    }

    /// <summary>
    /// Builds an adjacency list: node -> list of dependencies (nodes it depends on).
    /// Only includes dependencies that exist in the statement set.
    /// </summary>
    internal static Dictionary<string, List<string>> BuildDependencyGraph(List<DdlStatement> ddlStatements)
    {
        var knownObjects = ddlStatements.Select(s => s.ObjectName).ToHashSet();
        var graph = new Dictionary<string, List<string>>();

        foreach (var stmt in ddlStatements)
        {
            var validDeps = stmt.Dependencies
                .Where(d => knownObjects.Contains(d))
                .ToList();
            graph[stmt.ObjectName] = validDeps;
        }

        return graph;
    }

    /// <summary>
    /// DFS-based cycle detection matching Invoke-PgValidation.ps1 logic.
    /// Uses three-color marking: WHITE (unvisited), GRAY (in current path), BLACK (done).
    /// When a GRAY node is encountered during DFS, all nodes on the path from that node
    /// to the current node are part of a cycle.
    /// </summary>
    internal static HashSet<string> FindCircularDependencies(Dictionary<string, List<string>> graph)
    {
        const int WHITE = 0;
        // GRAY = 1 (in current DFS path), BLACK = 2 (fully processed)

        var color = new Dictionary<string, int>();
        var cycleMembers = new HashSet<string>();

        foreach (var node in graph.Keys)
        {
            color[node] = WHITE;
        }

        foreach (var node in graph.Keys)
        {
            if (color[node] == WHITE)
            {
                var stack = new Stack<string>();
                DfsVisit(node, graph, color, stack, cycleMembers);
            }
        }

        return cycleMembers;
    }

    private static void DfsVisit(
        string node,
        Dictionary<string, List<string>> graph,
        Dictionary<string, int> color,
        Stack<string> stack,
        HashSet<string> cycleMembers)
    {
        color[node] = 1; // GRAY
        stack.Push(node);

        if (graph.TryGetValue(node, out var deps))
        {
            foreach (var dep in deps)
            {
                if (!color.ContainsKey(dep))
                {
                    // Dependency not in our graph - skip
                    continue;
                }

                if (color[dep] == 1) // GRAY - back edge found
                {
                    // Found a cycle - collect all nodes on the stack from dep to node
                    var cycleNodes = new List<string> { node };
                    foreach (var stackItem in stack)
                    {
                        if (stackItem == dep)
                        {
                            cycleNodes.Add(stackItem);
                            break;
                        }
                        cycleNodes.Add(stackItem);
                    }
                    foreach (var cn in cycleNodes)
                    {
                        cycleMembers.Add(cn);
                    }
                }
                else if (color[dep] == 0) // WHITE
                {
                    DfsVisit(dep, graph, color, stack, cycleMembers);
                }
            }
        }

        stack.Pop();
        color[node] = 2; // BLACK
    }

    /// <summary>
    /// Topological sort excluding cycle members (Kahn's algorithm).
    /// Returns objects in dependency order (dependencies first).
    /// </summary>
    internal static List<string> GetTopologicalSort(
        Dictionary<string, List<string>> graph,
        HashSet<string> cycleMembers)
    {
        // Filter out cycle members
        var filteredGraph = new Dictionary<string, List<string>>();
        foreach (var kvp in graph)
        {
            if (!cycleMembers.Contains(kvp.Key))
            {
                var filteredDeps = kvp.Value
                    .Where(d => !cycleMembers.Contains(d))
                    .ToList();
                filteredGraph[kvp.Key] = filteredDeps;
            }
        }

        // Kahn's algorithm
        // graph[A] = [B, C] means A depends on B and C
        // In-degree: how many dependencies a node has (that exist in the filtered graph)
        var inDegree = new Dictionary<string, int>();
        foreach (var node in filteredGraph.Keys)
        {
            if (!inDegree.ContainsKey(node))
                inDegree[node] = 0;
        }

        foreach (var node in filteredGraph.Keys)
        {
            foreach (var dep in filteredGraph[node])
            {
                if (filteredGraph.ContainsKey(dep))
                {
                    // node depends on dep, so node's in-degree increases
                    inDegree[node] = inDegree.GetValueOrDefault(node, 0) + 1;
                }
            }
        }

        // Start with nodes that have no dependencies (in-degree 0)
        var queue = new Queue<string>();
        foreach (var kvp in inDegree)
        {
            if (kvp.Value == 0)
                queue.Enqueue(kvp.Key);
        }

        var sorted = new List<string>();
        while (queue.Count > 0)
        {
            var current = queue.Dequeue();
            sorted.Add(current);

            // Find all nodes that depend on current
            foreach (var node in filteredGraph.Keys)
            {
                if (filteredGraph[node].Contains(current))
                {
                    inDegree[node]--;
                    if (inDegree[node] == 0)
                        queue.Enqueue(node);
                }
            }
        }

        // Add any remaining nodes not yet sorted (shouldn't happen without cycles in filtered graph)
        foreach (var node in filteredGraph.Keys)
        {
            if (!sorted.Contains(node))
                sorted.Add(node);
        }

        return sorted;
    }

    /// <summary>
    /// Simple syntax-only validation for non-cycle objects.
    /// Checks basic DDL structure (non-empty, starts with valid keyword).
    /// </summary>
    private static ValidationResult ValidateSyntaxOnly(DdlStatement stmt)
    {
        if (string.IsNullOrWhiteSpace(stmt.Ddl))
        {
            return new ValidationResult(
                ObjectName: stmt.ObjectName,
                Status: "fail-syntax",
                ErrorMessage: "DDL statement is empty or null",
                ErrorLineNumber: 1,
                ValidationMode: "syntax-only"
            );
        }

        // Basic check: DDL should start with a valid PostgreSQL keyword
        var trimmed = stmt.Ddl.Trim();
        var validStarts = new[]
        {
            "CREATE", "ALTER", "DROP", "DO", "BEGIN",
            "COMMENT", "GRANT", "REVOKE", "SET", "INSERT"
        };

        var startsValid = validStarts.Any(k =>
            trimmed.StartsWith(k, StringComparison.OrdinalIgnoreCase));

        if (!startsValid)
        {
            return new ValidationResult(
                ObjectName: stmt.ObjectName,
                Status: "fail-syntax",
                ErrorMessage: "DDL does not start with a valid PostgreSQL statement keyword",
                ErrorLineNumber: 1,
                ValidationMode: "syntax-only"
            );
        }

        return new ValidationResult(
            ObjectName: stmt.ObjectName,
            Status: "pass",
            ErrorMessage: null,
            ErrorLineNumber: null,
            ValidationMode: "syntax-only"
        );
    }
}

#endregion

#region FsCheck Generators

/// <summary>
/// Generates dependency graph scenarios with a mix of cyclic and acyclic graphs.
/// </summary>
public class DependencyGraphArbitrary
{
    public static Arbitrary<DependencyGraphScenario> ArbitraryScenario()
    {
        var gen = Gen.Frequency(
            Tuple.Create(3, GenGraphWithCycle()),
            Tuple.Create(2, GenDag()),
            Tuple.Create(2, GenMixedGraph()));

        return Arb.From(gen);
    }

    /// <summary>
    /// Generates a graph that contains at least one intentional cycle.
    /// Example: A → B → C → A
    /// </summary>
    private static Gen<DependencyGraphScenario> GenGraphWithCycle()
    {
        return from cycleSize in Gen.Choose(2, 5)
               from extraNodes in Gen.Choose(0, 4)
               from seed in Gen.Choose(1, 10000)
               select BuildCyclicGraph(cycleSize, extraNodes, seed);
    }

    /// <summary>
    /// Generates a directed acyclic graph (DAG) with no cycles.
    /// Objects are numbered so dependencies only point to lower-numbered objects.
    /// </summary>
    private static Gen<DependencyGraphScenario> GenDag()
    {
        return from nodeCount in Gen.Choose(2, 8)
               from edgeDensity in Gen.Choose(1, 3)
               from seed in Gen.Choose(1, 10000)
               select BuildDag(nodeCount, edgeDensity, seed);
    }

    /// <summary>
    /// Generates a mixed graph: some objects in a cycle, others in a DAG portion.
    /// </summary>
    private static Gen<DependencyGraphScenario> GenMixedGraph()
    {
        return from cycleSize in Gen.Choose(2, 4)
               from dagSize in Gen.Choose(2, 5)
               from seed in Gen.Choose(1, 10000)
               select BuildMixedGraph(cycleSize, dagSize, seed);
    }

    private static DependencyGraphScenario BuildCyclicGraph(int cycleSize, int extraNodes, int seed)
    {
        var rng = new System.Random(seed);
        var scenario = new DependencyGraphScenario();
        var objectNames = new List<string>();

        // Create cycle nodes: node_0 → node_1 → ... → node_(n-1) → node_0
        for (int i = 0; i < cycleSize; i++)
        {
            objectNames.Add($"dbo.CycleObj_{seed}_{i}");
        }

        // Add cycle objects with circular dependencies
        for (int i = 0; i < cycleSize; i++)
        {
            int depIndex = (i + 1) % cycleSize; // each depends on the next, last depends on first
            scenario.DdlStatements.Add(new DdlStatement(
                ObjectName: objectNames[i],
                ObjectType: "Table",
                Ddl: $"CREATE TABLE {objectNames[i]} (id INTEGER PRIMARY KEY);",
                Dependencies: new List<string> { objectNames[depIndex] }
            ));
            scenario.ExpectedCycleMembers.Add(objectNames[i]);
        }

        // Add extra non-cycle nodes that don't participate in cycles
        for (int i = 0; i < extraNodes; i++)
        {
            var extraName = $"dbo.ExtraObj_{seed}_{i}";
            // Extra nodes may depend on cycle nodes but do NOT create new cycles
            var deps = new List<string>();
            // Some extra nodes have no dependencies at all
            if (rng.NextDouble() < 0.3 && objectNames.Count > 0)
            {
                // Depend on one of the existing objects, but not creating a back-edge
                // Since these are extra nodes with no one depending on them, no cycle forms
                deps.Add(objectNames[rng.Next(objectNames.Count)]);
            }
            scenario.DdlStatements.Add(new DdlStatement(
                ObjectName: extraName,
                ObjectType: "View",
                Ddl: $"CREATE VIEW {extraName} AS SELECT 1 AS val;",
                Dependencies: deps
            ));
        }

        return scenario;
    }

    private static DependencyGraphScenario BuildDag(int nodeCount, int edgeDensity, int seed)
    {
        var rng = new System.Random(seed);
        var scenario = new DependencyGraphScenario();

        var objectNames = new List<string>();
        for (int i = 0; i < nodeCount; i++)
        {
            objectNames.Add($"dbo.DagObj_{seed}_{i}");
        }

        // DAG: node i can only depend on nodes with index < i (ensures no cycles)
        for (int i = 0; i < nodeCount; i++)
        {
            var deps = new List<string>();
            if (i > 0)
            {
                // Add up to edgeDensity dependencies on earlier nodes
                int numDeps = Math.Min(edgeDensity, i);
                var possibleDeps = Enumerable.Range(0, i).ToList();
                for (int d = 0; d < numDeps && possibleDeps.Count > 0; d++)
                {
                    if (rng.NextDouble() < 0.6)
                    {
                        int idx = rng.Next(possibleDeps.Count);
                        deps.Add(objectNames[possibleDeps[idx]]);
                        possibleDeps.RemoveAt(idx);
                    }
                }
            }

            scenario.DdlStatements.Add(new DdlStatement(
                ObjectName: objectNames[i],
                ObjectType: i % 2 == 0 ? "Table" : "View",
                Ddl: $"CREATE TABLE {objectNames[i]} (id INTEGER PRIMARY KEY);",
                Dependencies: deps
            ));
        }

        // DAG has no expected cycle members
        return scenario;
    }

    private static DependencyGraphScenario BuildMixedGraph(int cycleSize, int dagSize, int seed)
    {
        var rng = new System.Random(seed);
        var scenario = new DependencyGraphScenario();

        // Part 1: Create a cycle
        var cycleNames = new List<string>();
        for (int i = 0; i < cycleSize; i++)
        {
            cycleNames.Add($"dbo.MixCycle_{seed}_{i}");
        }

        for (int i = 0; i < cycleSize; i++)
        {
            int depIndex = (i + 1) % cycleSize;
            scenario.DdlStatements.Add(new DdlStatement(
                ObjectName: cycleNames[i],
                ObjectType: "StoredProcedure",
                Ddl: $"CREATE OR REPLACE FUNCTION {cycleNames[i]}() RETURNS void AS $$ BEGIN END; $$ LANGUAGE plpgsql;",
                Dependencies: new List<string> { cycleNames[depIndex] }
            ));
            scenario.ExpectedCycleMembers.Add(cycleNames[i]);
        }

        // Part 2: Create a DAG portion (no cycles)
        var dagNames = new List<string>();
        for (int i = 0; i < dagSize; i++)
        {
            dagNames.Add($"dbo.MixDag_{seed}_{i}");
        }

        for (int i = 0; i < dagSize; i++)
        {
            var deps = new List<string>();
            if (i > 0 && rng.NextDouble() < 0.5)
            {
                // Depend on an earlier DAG node only
                deps.Add(dagNames[rng.Next(i)]);
            }

            scenario.DdlStatements.Add(new DdlStatement(
                ObjectName: dagNames[i],
                ObjectType: "Table",
                Ddl: $"CREATE TABLE {dagNames[i]} (id INTEGER PRIMARY KEY);",
                Dependencies: deps
            ));
        }

        return scenario;
    }
}

/// <summary>
/// Generates only DAG (acyclic) dependency graph scenarios for testing no-cycle cases.
/// </summary>
public class DagOnlyArbitrary
{
    public static Arbitrary<DependencyGraphScenario> ArbitraryScenario()
    {
        var gen = from nodeCount in Gen.Choose(2, 10)
                  from edgeDensity in Gen.Choose(1, 3)
                  from seed in Gen.Choose(1, 10000)
                  select BuildDag(nodeCount, edgeDensity, seed);

        return Arb.From(gen);
    }

    private static DependencyGraphScenario BuildDag(int nodeCount, int edgeDensity, int seed)
    {
        var rng = new System.Random(seed);
        var scenario = new DependencyGraphScenario();

        var objectNames = new List<string>();
        for (int i = 0; i < nodeCount; i++)
        {
            objectNames.Add($"dbo.DagOnly_{seed}_{i}");
        }

        // DAG: node i can only depend on nodes with index < i (ensures no cycles)
        for (int i = 0; i < nodeCount; i++)
        {
            var deps = new List<string>();
            if (i > 0)
            {
                int numDeps = Math.Min(edgeDensity, i);
                var possibleDeps = Enumerable.Range(0, i).ToList();
                for (int d = 0; d < numDeps && possibleDeps.Count > 0; d++)
                {
                    if (rng.NextDouble() < 0.6)
                    {
                        int idx = rng.Next(possibleDeps.Count);
                        deps.Add(objectNames[possibleDeps[idx]]);
                        possibleDeps.RemoveAt(idx);
                    }
                }
            }

            scenario.DdlStatements.Add(new DdlStatement(
                ObjectName: objectNames[i],
                ObjectType: i % 2 == 0 ? "Table" : "View",
                Ddl: $"CREATE TABLE {objectNames[i]} (id INTEGER PRIMARY KEY);",
                Dependencies: deps
            ));
        }

        // DAG has no expected cycle members
        return scenario;
    }
}

#endregion
