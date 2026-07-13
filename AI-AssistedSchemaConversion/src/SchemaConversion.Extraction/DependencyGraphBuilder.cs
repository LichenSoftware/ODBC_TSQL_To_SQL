using SchemaConversion.Core.Models;

namespace SchemaConversion.Extraction;

/// <summary>
/// Builds a dependency graph from schema objects and provides topological ordering
/// with cycle detection.
/// </summary>
public sealed class DependencyGraphBuilder
{
    /// <summary>
    /// Returns objects in dependency order (dependencies first).
    /// If cycles exist, returns the cycle members separately in the Cycles collection.
    /// Uses Kahn's algorithm for topological sort and Tarjan's SCC for cycle detection.
    /// </summary>
    public DependencyOrderResult GetProcessingOrder(IReadOnlyList<SchemaObject> objects)
    {
        if (objects.Count == 0)
        {
            return new DependencyOrderResult
            {
                Ordered = [],
                Cycles = []
            };
        }

        // Build adjacency structures keyed by qualified name
        var objectsByName = new Dictionary<string, SchemaObject>(StringComparer.OrdinalIgnoreCase);
        foreach (var obj in objects)
        {
            var key = $"{obj.SchemaName}.{obj.Name}";
            objectsByName.TryAdd(key, obj);
        }

        // Build adjacency list: edges from object -> its dependencies
        var adjacency = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        var inDegree = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        foreach (var obj in objects)
        {
            var key = $"{obj.SchemaName}.{obj.Name}";
            if (!adjacency.ContainsKey(key))
            {
                adjacency[key] = [];
            }
            if (!inDegree.ContainsKey(key))
            {
                inDegree[key] = 0;
            }
        }

        // For topological sort: edge from dependency to dependent
        // (i.e., dependency must come first)
        var reverseAdj = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var key in adjacency.Keys)
        {
            reverseAdj[key] = [];
        }

        foreach (var obj in objects)
        {
            var key = $"{obj.SchemaName}.{obj.Name}";
            foreach (var dep in obj.DependsOn)
            {
                // Only consider dependencies that exist in our object set
                if (!objectsByName.ContainsKey(dep))
                {
                    continue;
                }

                adjacency[key].Add(dep);

                if (!reverseAdj.ContainsKey(dep))
                {
                    reverseAdj[dep] = [];
                }
                reverseAdj[dep].Add(key);
                inDegree[key] = inDegree.GetValueOrDefault(key) + 1;
            }
        }

        // Detect cycles using Tarjan's SCC algorithm
        var cycles = FindCycles(objectsByName.Keys.ToList(), adjacency);

        // Identify nodes that are part of cycles
        var cycleNodes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var cycle in cycles)
        {
            foreach (var node in cycle)
            {
                cycleNodes.Add(node);
            }
        }

        // Kahn's algorithm for topological sort (excluding cycle nodes)
        var sorted = TopologicalSort(objectsByName.Keys.ToList(), adjacency, reverseAdj, cycleNodes);

        // Map results back to SchemaObject instances
        var ordered = sorted
            .Where(key => objectsByName.ContainsKey(key))
            .Select(key => objectsByName[key])
            .ToList();

        var cycleObjects = cycles
            .Select(cycle => (IReadOnlyList<SchemaObject>)cycle
                .Where(key => objectsByName.ContainsKey(key))
                .Select(key => objectsByName[key])
                .ToList())
            .Where(c => c.Count > 0)
            .ToList();

        return new DependencyOrderResult
        {
            Ordered = ordered,
            Cycles = cycleObjects
        };
    }

    /// <summary>
    /// Topological sort using Kahn's algorithm.
    /// Excludes nodes that are part of cycles.
    /// </summary>
    private static List<string> TopologicalSort(
        List<string> nodes,
        Dictionary<string, List<string>> adjacency,
        Dictionary<string, List<string>> reverseAdj,
        HashSet<string> cycleNodes)
    {
        // Compute in-degree for non-cycle nodes
        var inDegree = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var node in nodes)
        {
            if (cycleNodes.Contains(node))
            {
                continue;
            }
            inDegree[node] = 0;
        }

        foreach (var node in nodes)
        {
            if (cycleNodes.Contains(node))
            {
                continue;
            }

            foreach (var dep in adjacency.GetValueOrDefault(node, []))
            {
                if (!cycleNodes.Contains(dep) && inDegree.ContainsKey(dep))
                {
                    // dep is a dependency of node, so node has in-degree from dep
                    // Actually: edge from dep -> node in processing order
                    // inDegree should count how many deps each node has
                }
            }
        }

        // Recompute: inDegree[node] = number of dependencies node has (within non-cycle set)
        foreach (var node in inDegree.Keys.ToList())
        {
            var depCount = adjacency.GetValueOrDefault(node, [])
                .Count(dep => inDegree.ContainsKey(dep));
            inDegree[node] = depCount;
        }

        var queue = new Queue<string>();
        foreach (var (node, degree) in inDegree)
        {
            if (degree == 0)
            {
                queue.Enqueue(node);
            }
        }

        var result = new List<string>();

        while (queue.Count > 0)
        {
            var current = queue.Dequeue();
            result.Add(current);

            // Find all nodes that depend on current
            foreach (var dependent in reverseAdj.GetValueOrDefault(current, []))
            {
                if (!inDegree.ContainsKey(dependent))
                {
                    continue;
                }

                inDegree[dependent]--;
                if (inDegree[dependent] == 0)
                {
                    queue.Enqueue(dependent);
                }
            }
        }

        return result;
    }

    /// <summary>
    /// Finds strongly connected components (cycles) using Tarjan's algorithm.
    /// Returns only SCCs with more than one node (true cycles).
    /// </summary>
    private static List<List<string>> FindCycles(
        List<string> nodes,
        Dictionary<string, List<string>> adjacency)
    {
        var index = 0;
        var stack = new Stack<string>();
        var onStack = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        var indices = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var lowLinks = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        var sccs = new List<List<string>>();

        foreach (var node in nodes)
        {
            if (!indices.ContainsKey(node))
            {
                StrongConnect(node, ref index, stack, onStack, indices, lowLinks, adjacency, sccs);
            }
        }

        // Only return SCCs with more than 1 node (actual cycles)
        // Also include single-node self-loops
        var cycles = new List<List<string>>();
        foreach (var scc in sccs)
        {
            if (scc.Count > 1)
            {
                cycles.Add(scc);
            }
            else if (scc.Count == 1)
            {
                // Check for self-loop
                var node = scc[0];
                if (adjacency.GetValueOrDefault(node, [])
                    .Any(dep => string.Equals(dep, node, StringComparison.OrdinalIgnoreCase)))
                {
                    cycles.Add(scc);
                }
            }
        }

        return cycles;
    }

    private static void StrongConnect(
        string node,
        ref int index,
        Stack<string> stack,
        HashSet<string> onStack,
        Dictionary<string, int> indices,
        Dictionary<string, int> lowLinks,
        Dictionary<string, List<string>> adjacency,
        List<List<string>> sccs)
    {
        indices[node] = index;
        lowLinks[node] = index;
        index++;
        stack.Push(node);
        onStack.Add(node);

        foreach (var neighbor in adjacency.GetValueOrDefault(node, []))
        {
            if (!indices.ContainsKey(neighbor))
            {
                // Neighbor not yet visited
                StrongConnect(neighbor, ref index, stack, onStack, indices, lowLinks, adjacency, sccs);
                lowLinks[node] = Math.Min(lowLinks[node], lowLinks[neighbor]);
            }
            else if (onStack.Contains(neighbor))
            {
                // Neighbor is on stack, hence in current SCC
                lowLinks[node] = Math.Min(lowLinks[node], indices[neighbor]);
            }
        }

        // If node is a root of an SCC
        if (lowLinks[node] == indices[node])
        {
            var scc = new List<string>();
            string w;
            do
            {
                w = stack.Pop();
                onStack.Remove(w);
                scc.Add(w);
            } while (!string.Equals(w, node, StringComparison.OrdinalIgnoreCase));

            sccs.Add(scc);
        }
    }
}
