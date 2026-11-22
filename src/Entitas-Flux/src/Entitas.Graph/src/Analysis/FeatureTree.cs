using System;
using System.Collections.Generic;
using System.Linq;

namespace Entitas.Graph.Analysis
{
    /// <summary>
    /// Registration and execution order from the contains edges: which feature registers each
    /// system at which position, which features are roots (registered by no feature — the
    /// project says which, the tool knows no names), and where every system runs under each
    /// root, counting systems in the order Entitas executes them.
    /// </summary>
    public static class FeatureTree
    {
        public static void Place(EcsGraph graph)
        {
            var children = graph.Edges
                .Where(e => e.Relation == Relation.Contains)
                .GroupBy(e => e.Source, StringComparer.Ordinal)
                .ToDictionary(g => g.Key, g => g.OrderBy(e => e.Order).ToList(), StringComparer.Ordinal);

            var registered = new HashSet<string>(StringComparer.Ordinal);
            foreach (var edge in graph.Edges.Where(e => e.Relation == Relation.Contains))
            {
                registered.Add(edge.Target);
                graph.Node(edge.Target)?.RegisteredIn.Add(new Registration { Feature = edge.Source, Index = edge.Order });
            }

            // A generated feature nobody registers (GameCleanupSystems, GameWatchedCleanupSystems…)
            // is not a root: its systems simply never run, which the report says.
            foreach (var root in graph.Nodes.Where(n => n.Kind == NodeKind.Feature && !n.Generated && !registered.Contains(n.Id) && children.ContainsKey(n.Id)))
            {
                root.Root = true;
                var order = 0;
                Visit(root.Id, new List<string> { root.Id });

                void Visit(string featureId, List<string> chain)
                {
                    if (!children.TryGetValue(featureId, out var edges))
                        return;

                    foreach (var edge in edges)
                    {
                        var child = graph.Node(edge.Target);
                        if (child == null || chain.Contains(child.Id))
                            continue;

                        if (child.Kind == NodeKind.System)
                        {
                            child.Placements.Add(new Placement { Root = root.Id, Order = ++order, Chain = new List<string>(chain) });
                        }
                        else if (child.Kind == NodeKind.Feature)
                        {
                            child.Placements.Add(new Placement { Root = root.Id, Order = order + 1, Chain = new List<string>(chain) });
                            Visit(child.Id, new List<string>(chain) { child.Id });
                        }
                    }
                }
            }
        }
    }
}
