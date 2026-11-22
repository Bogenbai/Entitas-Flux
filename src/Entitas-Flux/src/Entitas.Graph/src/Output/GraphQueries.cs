using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace Entitas.Graph.Output
{
    /// <summary>A name that matched several nodes; nothing is guessed (exit code 3).</summary>
    public sealed class AmbiguousNameException : Exception
    {
        public IReadOnlyList<GraphNode> Candidates { get; }
        public AmbiguousNameException(string name, IReadOnlyList<GraphNode> candidates)
            : base($"'{name}' matches {candidates.Count} nodes") => Candidates = candidates;
    }

    /// <summary>A name that matched nothing (exit code 4), with the closest labels.</summary>
    public sealed class NodeNotFoundException : Exception
    {
        public IReadOnlyList<GraphNode> Suggestions { get; }
        public NodeNotFoundException(string name, IReadOnlyList<GraphNode> suggestions)
            : base($"no node named '{name}'") => Suggestions = suggestions;
    }

    /// <summary>
    /// The questions entitas-graph answers. Output is plain text meant to be read by a
    /// person or pasted to an assistant: one line per fact, the code location at the end.
    /// </summary>
    public sealed class GraphQueries
    {
        readonly EcsGraph _graph;

        public GraphQueries(EcsGraph graph) => _graph = graph;

        // -- resolving names --------------------------------------------------------------

        public GraphNode Resolve(string name, string? kind = null)
        {
            var candidates = _graph.Nodes.Where(n => kind == null || n.Kind == kind).ToList();
            var trimmed = name.Trim();
            var bare = trimmed.EndsWith("Component", StringComparison.Ordinal) && trimmed.Length > "Component".Length
                ? trimmed.Substring(0, trimmed.Length - "Component".Length)
                : trimmed;

            var exact = candidates.Where(n => n.Id == trimmed || n.FullName == trimmed).ToList();
            if (exact.Count == 1) return exact[0];

            var byLabel = candidates.Where(n => string.Equals(n.Label, trimmed, StringComparison.OrdinalIgnoreCase) ||
                                                string.Equals(n.Label, bare, StringComparison.OrdinalIgnoreCase)).ToList();
            if (byLabel.Count == 1) return byLabel[0];
            if (byLabel.Count > 1)
            {
                // A component and a type sharing a name is common (the component class itself is a type): prefer the ECS meaning.
                var preferred = byLabel.Where(n => n.Kind != NodeKind.Type).ToList();
                if (preferred.Count == 1) return preferred[0];
                throw new AmbiguousNameException(name, byLabel);
            }

            var suggestions = candidates
                .Where(n => n.Label.IndexOf(bare, StringComparison.OrdinalIgnoreCase) >= 0 ||
                            bare.IndexOf(n.Label, StringComparison.OrdinalIgnoreCase) >= 0 && n.Label.Length > 3)
                .OrderBy(n => Math.Abs(n.Label.Length - bare.Length))
                .Take(8)
                .ToList();
            throw new NodeNotFoundException(name, suggestions);
        }

        // -- ECS questions ---------------------------------------------------------------------

        public string WhoCreates(string componentName) => Users(componentName, "created by", Relation.Creates);

        public string WhoReads(string componentName) => Users(componentName, "read by", Relation.Reads, Relation.Reacts, Relation.Filters);

        string Users(string componentName, string verb, params string[] relations)
        {
            var component = Resolve(componentName, NodeKind.Component);
            var edges = _graph.Incoming(component.Id).Where(e => relations.Contains(e.Relation)).ToList();
            var sources = edges.Select(e => e.Source).Distinct().ToList();

            var sb = new StringBuilder();
            sb.AppendLine($"{component.Label} — {verb} {sources.Count} ({Count(edges, Confidence.Extracted)} direct, {Count(edges, Confidence.Inferred)} inferred)");
            foreach (var group in edges.GroupBy(e => e.Source).OrderBy(g => SortKey(g.Key)))
            {
                var node = _graph.Node(group.Key)!;
                sb.AppendLine($"  {node.Label}  [{Describe(group)}]  {Placement(node)}");
                foreach (var edge in group.OrderBy(e => e.Line))
                    sb.AppendLine($"      {edge.Relation} via {edge.Via}{Through(edge)}  {Location(edge)}");
            }

            return sb.ToString();
        }

        /// <summary>
        /// Everything that happens to a component, per root feature in execution order:
        /// who creates it, who reads or filters on it, who removes or destroys it.
        /// </summary>
        public string Lifecycle(string componentName)
        {
            var component = Resolve(componentName, NodeKind.Component);
            var edges = _graph.Incoming(component.Id).Where(e => e.Relation != Relation.Contains).ToList();

            var sb = new StringBuilder();
            sb.AppendLine($"{component.Label}{Attributes(component)}");
            sb.AppendLine($"  created by {Distinct(edges, Relation.Creates)} · read by {Distinct(edges, Relation.Reads, Relation.Reacts)} · " +
                          $"filtered by {Distinct(edges, Relation.Filters)} · removed by {Distinct(edges, Relation.Removes)} · destroyed by {Distinct(edges, Relation.Destroys)}");

            var bySource = edges.GroupBy(e => e.Source).ToDictionary(g => g.Key, g => g.ToList(), StringComparer.Ordinal);
            var roots = bySource.Keys
                .SelectMany(id => _graph.Node(id)!.Placements.Select(p => p.Root))
                .Distinct()
                .OrderBy(r => r, StringComparer.Ordinal)
                .ToList();

            foreach (var root in roots)
            {
                sb.AppendLine();
                sb.AppendLine($"{Label(root)}:");
                var steps = bySource
                    .SelectMany(pair => _graph.Node(pair.Key)!.Placements.Where(p => p.Root == root).Select(p => (Placement: p, Node: _graph.Node(pair.Key)!, Edges: pair.Value)))
                    .OrderBy(s => s.Placement.Order);
                foreach (var step in steps)
                {
                    var feature = step.Placement.Chain.Count > 0 ? Label(step.Placement.Chain[step.Placement.Chain.Count - 1]) : string.Empty;
                    sb.AppendLine($"  #{step.Placement.Order,-4} {Verbs(step.Edges),-22} {step.Node.Label}  ({feature}){Inferred(step.Edges)}");
                }
            }

            var unplaced = bySource.Keys.Select(id => _graph.Node(id)!).Where(n => n.Placements.Count == 0).OrderBy(n => n.Label).ToList();
            if (unplaced.Count > 0)
            {
                sb.AppendLine();
                sb.AppendLine("Not in any feature (queries, services, unregistered systems):");
                foreach (var node in unplaced)
                    sb.AppendLine($"  {Verbs(bySource[node.Id]),-22} {node.Label}  [{node.Kind}]{Inferred(bySource[node.Id])}");
            }

            return sb.ToString();
        }

        public string SystemsOf(string featureName)
        {
            var feature = Resolve(featureName, NodeKind.Feature);
            var sb = new StringBuilder();
            sb.AppendLine($"{feature.Label}{(feature.Root ? " (root)" : string.Empty)} — {CountSystems(feature.Id, new HashSet<string>())} systems");
            Tree(feature.Id, "  ", new HashSet<string> { feature.Id });
            return sb.ToString();

            void Tree(string id, string indent, HashSet<string> seen)
            {
                foreach (var edge in _graph.Outgoing(id).Where(e => e.Relation == Relation.Contains).OrderBy(e => e.Order))
                {
                    var child = _graph.Node(edge.Target)!;
                    var interfaces = child.Interfaces.Count > 0 ? $"  [{string.Join(", ", child.Interfaces)}]" : string.Empty;
                    sb.AppendLine($"{indent}{edge.Order,3}. {child.Label}{(child.Kind == NodeKind.Feature ? " (feature)" : interfaces)}");
                    if (child.Kind == NodeKind.Feature && seen.Add(child.Id))
                        Tree(child.Id, indent + "     ", seen);
                }
            }
        }

        int CountSystems(string id, HashSet<string> seen)
        {
            var total = 0;
            foreach (var edge in _graph.Outgoing(id).Where(e => e.Relation == Relation.Contains))
            {
                var child = _graph.Node(edge.Target)!;
                if (child.Kind == NodeKind.System) total++;
                else if (seen.Add(child.Id)) total += CountSystems(child.Id, seen);
            }
            return total;
        }

        // -- general questions -----------------------------------------------------------------

        public string Explain(string name)
        {
            var node = Resolve(name);
            var sb = new StringBuilder();
            sb.AppendLine($"{node.Label}  [{node.Kind}{(node.Generated ? ", generated" : string.Empty)}{(node.Root ? ", root" : string.Empty)}]");
            if (node.FullName != null) sb.AppendLine($"  type: {node.FullName}");
            if (node.SourceFile != null) sb.AppendLine($"  source: {node.SourceFile}:{node.Line}");
            if (node.Kind == NodeKind.Component)
            {
                sb.AppendLine($"  contexts: {string.Join(", ", node.Contexts)}; {(node.Flag ? "flag" : "data: " + string.Join(", ", node.Fields))}");
                if (node.Attributes.Count > 0) sb.AppendLine($"  attributes: {string.Join(", ", node.Attributes)}");
                if (node.Role != null) sb.AppendLine($"  role: {node.Role} (by name)");
            }
            if (node.Interfaces.Count > 0) sb.AppendLine($"  runs: {string.Join(", ", node.Interfaces)}");
            foreach (var registration in node.RegisteredIn)
                sb.AppendLine($"  registered: {Label(registration.Feature)} #{registration.Index}");
            foreach (var placement in node.Placements)
                sb.AppendLine($"  runs under {Label(placement.Root)} at #{placement.Order} via {string.Join(" → ", placement.Chain.Select(Label))}");

            AppendEdges(sb, "  ", _graph.Outgoing(node.Id), outgoing: true);
            AppendEdges(sb, "  ", _graph.Incoming(node.Id), outgoing: false);
            return sb.ToString();
        }

        void AppendEdges(StringBuilder sb, string indent, IEnumerable<GraphEdge> edges, bool outgoing)
        {
            foreach (var group in edges.GroupBy(e => e.Relation).OrderBy(g => g.Key, StringComparer.Ordinal))
            {
                var list = group.OrderBy(e => outgoing ? Label(e.Target) : Label(e.Source)).ToList();
                sb.AppendLine($"{indent}{(outgoing ? group.Key : Passive(group.Key))} ({list.Count}):");
                foreach (var edge in list.Take(25))
                {
                    var other = outgoing ? edge.Target : edge.Source;
                    sb.AppendLine($"{indent}  {Label(other)}  via {edge.Via}{Through(edge)}  {Location(edge)}");
                }
                if (list.Count > 25)
                    sb.AppendLine($"{indent}  … {list.Count - 25} more (use --json for all)");
            }
        }

        /// <summary>
        /// The shortest chain of data flow from A to B: a system that creates a component leads
        /// to the systems reading it, a component leads to whoever reads it. Unlike a path
        /// through a shared base type, every hop means something happened.
        /// </summary>
        public string Path(string from, string to)
        {
            var a = Resolve(from);
            var b = Resolve(to);
            var forward = DataFlow(directed: true);
            var path = Bfs(forward, a.Id, b.Id);
            var undirected = false;
            if (path == null)
            {
                path = Bfs(DataFlow(directed: false), a.Id, b.Id);
                undirected = path != null;
            }

            if (path == null)
                return $"No data-flow path between {a.Label} and {b.Label}.\n";

            var sb = new StringBuilder();
            sb.AppendLine($"{a.Label} → {b.Label}: {path.Count} hops{(undirected ? " (no forward path; shown ignoring direction)" : string.Empty)}");
            foreach (var (edge, reversed) in path)
            {
                var left = reversed ? edge.Target : edge.Source;
                var right = reversed ? edge.Source : edge.Target;
                var relation = reversed ? Passive(edge.Relation) : edge.Relation;
                sb.AppendLine($"  {Label(left)} --{relation}--> {Label(right)}  [{edge.Confidence}] via {edge.Via}{Through(edge)}  {Location(edge)}");
            }
            return sb.ToString();
        }

        Dictionary<string, List<(GraphEdge Edge, bool Reversed, string Next)>> DataFlow(bool directed)
        {
            var adjacency = new Dictionary<string, List<(GraphEdge, bool, string)>>(StringComparer.Ordinal);
            void Link(string from, GraphEdge edge, bool reversed, string next)
            {
                if (!adjacency.TryGetValue(from, out var list)) adjacency[from] = list = new List<(GraphEdge, bool, string)>();
                list.Add((edge, reversed, next));
            }

            foreach (var edge in _graph.Edges)
            {
                // Writes flow from the system to the component; reads flow from the component to the system.
                var forward = edge.Relation is Relation.Creates or Relation.Removes or Relation.Destroys or Relation.Contains;
                if (forward) Link(edge.Source, edge, false, edge.Target);
                else Link(edge.Target, edge, true, edge.Source);

                if (!directed)
                {
                    if (forward) Link(edge.Target, edge, true, edge.Source);
                    else Link(edge.Source, edge, false, edge.Target);
                }
            }

            return adjacency;
        }

        static List<(GraphEdge Edge, bool Reversed)>? Bfs(Dictionary<string, List<(GraphEdge Edge, bool Reversed, string Next)>> adjacency, string from, string to)
        {
            var previous = new Dictionary<string, (string From, GraphEdge Edge, bool Reversed)>(StringComparer.Ordinal);
            var queue = new Queue<string>();
            queue.Enqueue(from);
            var seen = new HashSet<string>(StringComparer.Ordinal) { from };
            while (queue.Count > 0)
            {
                var current = queue.Dequeue();
                if (current == to) break;
                if (!adjacency.TryGetValue(current, out var next)) continue;
                foreach (var (edge, reversed, node) in next.OrderBy(n => n.Edge.Confidence == Confidence.Extracted ? 0 : 1))
                {
                    if (!seen.Add(node)) continue;
                    previous[node] = (current, edge, reversed);
                    queue.Enqueue(node);
                }
            }

            if (!previous.ContainsKey(to)) return null;
            var path = new List<(GraphEdge, bool)>();
            for (var node = to; node != from; node = previous[node].From)
                path.Add((previous[node].Edge, previous[node].Reversed));
            path.Reverse();
            return path;
        }

        /// <summary>Nodes matching the words (label or type name), best first, each with its connections summarised.</summary>
        public string Query(string text, int limit)
        {
            var terms = text.Split(new[] { ' ', ',', '.', '?' }, StringSplitOptions.RemoveEmptyEntries)
                .Where(t => t.Length > 1)
                .Select(t => t.ToLowerInvariant())
                .ToArray();
            if (terms.Length == 0)
                return "Nothing to search for.\n";

            var ranked = _graph.Nodes
                .Select(n => (Node: n, Score: terms.Count(t => n.Label.ToLowerInvariant().Contains(t) || (n.FullName ?? string.Empty).ToLowerInvariant().Contains(t))))
                .Where(x => x.Score > 0)
                .OrderByDescending(x => x.Score)
                .ThenByDescending(x => Degree(x.Node.Id))
                .ThenBy(x => x.Node.Label, StringComparer.Ordinal)
                .ToList();

            var sb = new StringBuilder();
            sb.AppendLine($"{ranked.Count} nodes match \"{text}\"{(ranked.Count > limit ? $"; showing {limit} (raise --limit for more)" : string.Empty)}");
            foreach (var (node, _) in ranked.Take(limit))
            {
                var relations = _graph.Outgoing(node.Id).GroupBy(e => e.Relation).Select(g => $"{g.Key} {g.Select(e => e.Target).Distinct().Count()}")
                    .Concat(_graph.Incoming(node.Id).GroupBy(e => e.Relation).Select(g => $"{Passive(g.Key)} {g.Select(e => e.Source).Distinct().Count()}"));
                sb.AppendLine($"  {node.Label}  [{node.Kind}]  {string.Join(", ", relations)}  {node.SourceFile}{(node.Line > 0 ? ":" + node.Line : string.Empty)}");
            }
            return sb.ToString();
        }

        // -- formatting helpers ------------------------------------------------------------------

        int Degree(string id) => _graph.Edges.Count(e => e.Source == id || e.Target == id);

        string Label(string id) => _graph.Node(id)?.Label ?? id;

        string Placement(GraphNode node)
        {
            if (node.Placements.Count == 0)
                return node.Kind == NodeKind.System ? "not registered in any feature" : $"[{node.Kind}]";

            return string.Join("; ", node.Placements.Select(p =>
            {
                var feature = p.Chain.Count > 0 ? p.Chain[p.Chain.Count - 1] : p.Root;
                var index = node.RegisteredIn.FirstOrDefault(r => r.Feature == feature)?.Index;
                return $"{Label(feature)}{(index != null ? " #" + index : string.Empty)}, {Label(p.Root)} order {p.Order}";
            }));
        }

        string SortKey(string id)
        {
            var node = _graph.Node(id)!;
            var placement = node.Placements.OrderBy(p => p.Root, StringComparer.Ordinal).FirstOrDefault();
            return placement == null ? "~" + node.Label : $"{placement.Root}\u0001{placement.Order:D6}";
        }

        static string Describe(IEnumerable<GraphEdge> edges) =>
            string.Join(", ", edges.Select(e => e.Via + (e.Confidence == Confidence.Inferred ? "*" : string.Empty)).Distinct());

        static string Verbs(IEnumerable<GraphEdge> edges) =>
            string.Join("+", edges.Select(e => e.Relation).Distinct().OrderBy(r => Array.IndexOf(VerbOrder, r)));

        static readonly string[] VerbOrder = { Relation.Creates, Relation.Reads, Relation.Reacts, Relation.Filters, Relation.Removes, Relation.Destroys };

        static string Inferred(IEnumerable<GraphEdge> edges)
        {
            var inferred = edges.Where(e => e.Confidence == Confidence.Inferred).Select(e => e.Through).Where(t => t != null).Distinct().ToList();
            return inferred.Count == 0 || edges.Any(e => e.Confidence == Confidence.Extracted)
                ? string.Empty
                : $"  — via {string.Join("; ", inferred)}";
        }

        string Attributes(GraphNode node)
        {
            var parts = new List<string> { string.Join("/", node.Contexts), node.Flag ? "flag" : "data" };
            parts.AddRange(node.Attributes);
            if (node.Role != null) parts.Add(node.Role);
            return $"  [{string.Join(", ", parts)}]";
        }

        static int Count(IEnumerable<GraphEdge> edges, string confidence) => edges.Where(e => e.Confidence == confidence).Select(e => e.Source).Distinct().Count();

        static int Distinct(IEnumerable<GraphEdge> edges, params string[] relations) =>
            edges.Where(e => relations.Contains(e.Relation)).Select(e => e.Source).Distinct().Count();

        static string Through(GraphEdge edge) => edge.Through == null ? string.Empty : $" ({edge.Through})";

        static string Location(GraphEdge edge) => edge.SourceFile == null ? string.Empty : $"{edge.SourceFile}:{edge.Line}";

        public static string Passive(string relation) => relation switch
        {
            Relation.Creates => "created by",
            Relation.Reads => "read by",
            Relation.Filters => "filtered by",
            Relation.Removes => "removed by",
            Relation.Destroys => "destroyed by",
            Relation.Reacts => "reacted to by",
            Relation.Contains => "registered by",
            _ => relation + " by"
        };
    }
}
