using System;
using System.Collections.Generic;
using System.Linq;

namespace Entitas.Graph.Output
{
    /// <summary>
    /// What `query` answers with: the nodes the words name (the seeds) and the part of the graph
    /// around them, walked breadth-first over data-flow edges in both directions — a component
    /// leads to whoever creates, reads or destroys it, a system to the components it touches.
    /// Feature registration is not walked: it says where a system runs, not what flows, and one
    /// step from a root feature would be the whole project. Pure data; <see cref="GraphQueries"/>
    /// turns it into text.
    ///
    /// On a real project a plain walk reaches most of the graph in two steps, through the few
    /// components everything touches (an Id, a position, the Event flag) and the systems that
    /// touch everything (a spawner creating 50 components through a factory). Two rules keep it
    /// about the question. A hub — a node other than a seed with more data-flow neighbours than
    /// the threshold — is reached but not walked through. And when the answer has to fit a
    /// budget, each node follows only its strongest links (<see cref="Build"/>'s linksPerNode),
    /// so that what is listed stays connected: every node in the answer hangs off a line above it.
    /// </summary>
    internal sealed class QuerySubgraph
    {
        public IReadOnlyList<string> Terms { get; private set; } = Array.Empty<string>();

        /// <summary>Every node the words match, best first: score, then degree, then label.</summary>
        public IReadOnlyList<SubgraphNode> Matches { get; private set; } = Array.Empty<SubgraphNode>();

        public IReadOnlyList<SubgraphNode> Seeds { get; private set; } = Array.Empty<SubgraphNode>();

        /// <summary>The subgraph in answer order: by distance from the seeds, then relevance.</summary>
        public IReadOnlyList<SubgraphNode> Nodes { get; private set; } = Array.Empty<SubgraphNode>();

        public int Depth { get; private set; }
        public int LinksPerNode { get; private set; }

        public int LinkCount => Nodes.Sum(n => n.Links.Count);

        /// <summary>Hubs in the subgraph: reached, not walked through.</summary>
        public IEnumerable<SubgraphNode> Hubs => Nodes.Where(n => n.Hub);

        /// <summary>The words worth matching: at least two characters, compared case-insensitively.</summary>
        public static string[] Words(string text) =>
            text.Split(new[] { ' ', '\t', ',', '.', '?', '!', ':', ';', '"', '\'', '(', ')' }, StringSplitOptions.RemoveEmptyEntries)
                .Where(t => t.Length > 1)
                .Select(t => t.ToLowerInvariant())
                .Distinct()
                .ToArray();

        public static QuerySubgraph Build(EcsGraph graph, string text, int seedLimit, int depth, int hubThreshold, int linksPerNode)
        {
            var index = new Index(graph, Words(text), hubThreshold);
            var result = new QuerySubgraph { Terms = index.Terms, Depth = depth, LinksPerNode = linksPerNode };
            if (index.Terms.Length == 0)
                return result;

            result.Matches = graph.Nodes
                .Select(index.Get)
                .Where(n => n.Score > 0)
                .OrderBy(n => n, Relevance)
                .ToList();

            // A feature has no data-flow edges, so as a seed it would be a lone line taking the
            // place of a system that does something; the systems it runs name it in their
            // placement. Only when nothing else matches do features (or loose types) seed.
            var candidates = result.Matches.Where(n => index.Flow(n.Node.Id).Count > 0).ToList();
            result.Seeds = (candidates.Count > 0 ? candidates : result.Matches).Take(Math.Max(1, seedLimit)).ToList();
            if (result.Seeds.Count == 0)
                return result;

            foreach (var seed in result.Seeds)
            {
                seed.Seed = true;
                seed.Hub = false;
                seed.Distance = 0;
            }

            result.Nodes = Walk(index, result.Seeds, depth, linksPerNode);
            return result;
        }

        /// <summary>
        /// Breadth-first, a level at a time, so a level can be put in answer order before any of
        /// its nodes lists a line. A node lists its links to nodes after it in that order (and to
        /// hubs before it, which list nothing), so each edge is printed once, from the side nearer
        /// the seeds; the links it lists are the ones the walk follows.
        /// </summary>
        static List<SubgraphNode> Walk(Index index, IReadOnlyList<SubgraphNode> seeds, int depth, int linksPerNode)
        {
            var order = new List<SubgraphNode>();
            var reached = seeds.ToDictionary(s => s.Node.Id, StringComparer.Ordinal);
            var level = seeds.ToList();

            for (var distance = 0; level.Count > 0; distance++)
            {
                level.Sort(Relevance);
                foreach (var node in level)
                {
                    node.Position = order.Count;
                    order.Add(node);
                }

                if (distance == depth)
                    break;

                var next = new List<SubgraphNode>();
                foreach (var node in level)
                {
                    node.Expanded = node.Seed || !node.Hub;
                    if (!node.Expanded)
                        continue;

                    var candidates = index.Flow(node.Node.Id)
                        .GroupBy(e => (Other: e.Source == node.Node.Id ? e.Target : e.Source, e.Relation, Outgoing: e.Source == node.Node.Id))
                        .Where(g => g.Key.Other != node.Node.Id)
                        .Where(g => !reached.TryGetValue(g.Key.Other, out var other) || other.Position > node.Position || !other.Expanded)
                        .Select(g => new SubgraphLink(index.Get(g.Key.Other), g.Key.Relation, g.Key.Outgoing,
                            g.OrderBy(e => e.Confidence == Confidence.Extracted ? 0 : 1).ThenBy(e => e.SourceFile, StringComparer.Ordinal).ThenBy(e => e.Line).ToList()))
                        .OrderBy(l => l, Strength(reached))
                        .ToList();

                    var listed = Math.Min(candidates.Count, linksPerNode);
                    for (var i = 0; i < candidates.Count; i++)
                    {
                        var link = candidates[i];
                        if (i >= listed)
                        {
                            node.LeftOut.Add(link);
                            continue;
                        }

                        node.Links.Add(link);
                        if (reached.ContainsKey(link.Other.Node.Id))
                            continue;

                        link.Other.Distance = distance + 1;
                        reached.Add(link.Other.Node.Id, link.Other);
                        next.Add(link.Other);
                    }
                }

                level = next;
            }

            return order;
        }

        /// <summary>Seeds and matches first, then specific nodes before ones everything touches.</summary>
        static readonly Comparer<SubgraphNode> Relevance = Comparer<SubgraphNode>.Create((a, b) =>
        {
            var c = b.Score.CompareTo(a.Score);
            if (c != 0) return c;
            c = b.Degree.CompareTo(a.Degree);
            if (c != 0) return c;
            c = string.CompareOrdinal(a.Node.Label, b.Node.Label);
            return c != 0 ? c : string.CompareOrdinal(a.Node.Id, b.Node.Id);
        });

        /// <summary>
        /// Which of a node's links survive a per-node cap, strongest first: to a seed; written in
        /// the system itself — a factory it calls can add forty components, none of which says
        /// what the system does; to a request or event (the hand-offs between systems); to a node
        /// whose name the words match, then one whose namespace does; to a node already in the
        /// answer (it shows how the pieces connect); to a specific node before a hub-like one —
        /// `creates Id` says little.
        /// </summary>
        static Comparer<SubgraphLink> Strength(Dictionary<string, SubgraphNode> reached) => Comparer<SubgraphLink>.Create((a, b) =>
        {
            var c = b.Other.Seed.CompareTo(a.Other.Seed);
            if (c != 0) return c;
            c = b.Extracted.CompareTo(a.Extracted);
            if (c != 0) return c;
            c = (b.Other.Node.Role != null).CompareTo(a.Other.Node.Role != null);
            if (c != 0) return c;
            c = b.Other.LabelScore.CompareTo(a.Other.LabelScore);
            if (c != 0) return c;
            c = b.Other.Score.CompareTo(a.Other.Score);
            if (c != 0) return c;
            c = reached.ContainsKey(b.Other.Node.Id).CompareTo(reached.ContainsKey(a.Other.Node.Id));
            if (c != 0) return c;
            c = a.Other.Neighbours.CompareTo(b.Other.Neighbours);
            if (c != 0) return c;
            c = string.CompareOrdinal(a.Other.Node.Label, b.Other.Node.Label);
            if (c != 0) return c;
            c = string.CompareOrdinal(a.Other.Node.Id, b.Other.Node.Id);
            if (c != 0) return c;
            c = string.CompareOrdinal(a.Relation, b.Relation);
            return c != 0 ? c : b.Outgoing.CompareTo(a.Outgoing);
        });

        /// <summary>Data-flow adjacency, degrees and scores, computed once per query.</summary>
        sealed class Index
        {
            public readonly string[] Terms;
            readonly int _hubThreshold;
            readonly Dictionary<string, List<GraphEdge>> _flow = new Dictionary<string, List<GraphEdge>>(StringComparer.Ordinal);
            readonly Dictionary<string, int> _degree = new Dictionary<string, int>(StringComparer.Ordinal);
            readonly Dictionary<string, SubgraphNode> _nodes = new Dictionary<string, SubgraphNode>(StringComparer.Ordinal);
            readonly EcsGraph _graph;

            public Index(EcsGraph graph, string[] terms, int hubThreshold)
            {
                _graph = graph;
                Terms = terms;
                _hubThreshold = hubThreshold;
                foreach (var edge in graph.Edges)
                {
                    _degree[edge.Source] = _degree.TryGetValue(edge.Source, out var s) ? s + 1 : 1;
                    _degree[edge.Target] = _degree.TryGetValue(edge.Target, out var t) ? t + 1 : 1;
                    if (edge.Relation == Relation.Contains)
                        continue;
                    Add(edge.Source, edge);
                    Add(edge.Target, edge);
                }
            }

            void Add(string id, GraphEdge edge)
            {
                if (!_flow.TryGetValue(id, out var list))
                    _flow[id] = list = new List<GraphEdge>();
                list.Add(edge);
            }

            public IReadOnlyList<GraphEdge> Flow(string id) => _flow.TryGetValue(id, out var list) ? list : (IReadOnlyList<GraphEdge>)Array.Empty<GraphEdge>();

            public SubgraphNode Get(string id) => Get(_graph.Node(id)!);

            public SubgraphNode Get(GraphNode node)
            {
                if (_nodes.TryGetValue(node.Id, out var existing))
                    return existing;

                var neighbours = Flow(node.Id).Select(e => e.Source == node.Id ? e.Target : e.Source).Distinct(StringComparer.Ordinal).Count();
                var created = new SubgraphNode
                {
                    Node = node,
                    Score = Terms.Count(t => node.Label.IndexOf(t, StringComparison.OrdinalIgnoreCase) >= 0 ||
                                             (node.FullName ?? string.Empty).IndexOf(t, StringComparison.OrdinalIgnoreCase) >= 0),
                    LabelScore = Terms.Count(t => node.Label.IndexOf(t, StringComparison.OrdinalIgnoreCase) >= 0),
                    Degree = _degree.TryGetValue(node.Id, out var degree) ? degree : 0,
                    Neighbours = neighbours,
                    Hub = neighbours > _hubThreshold,
                    Position = int.MaxValue
                };
                _nodes[node.Id] = created;
                return created;
            }
        }
    }

    /// <summary>A node of a query's subgraph and the lines the answer lists under it.</summary>
    internal sealed class SubgraphNode
    {
        public GraphNode Node = null!;

        /// <summary>How many of the query's words its label or type name contains.</summary>
        public int Score;

        /// <summary>How many its label alone contains: a namespace match is the weaker hint.</summary>
        public int LabelScore;

        /// <summary>Every edge it has, registrations included.</summary>
        public int Degree;

        /// <summary>Distinct data-flow neighbours: what makes a hub.</summary>
        public int Neighbours;

        /// <summary>Steps from the nearest seed.</summary>
        public int Distance;

        /// <summary>Its place in the answer; int.MaxValue until its level is ordered.</summary>
        public int Position;

        public bool Seed;
        public bool Hub;

        /// <summary>Walked through: a seed, or a node closer than the depth that is no hub.</summary>
        public bool Expanded;

        public readonly List<SubgraphLink> Links = new List<SubgraphLink>();

        /// <summary>Links the per-node cap left out — counted on the node's last line.</summary>
        public readonly List<SubgraphLink> LeftOut = new List<SubgraphLink>();
    }

    /// <summary>Every edge between a node and one other node in one relation and direction: one line of the answer.</summary>
    internal sealed class SubgraphLink
    {
        public readonly SubgraphNode Other;
        public readonly string Relation;

        /// <summary>The listing node is the edge's source: `creates X` rather than `created by X`.</summary>
        public readonly bool Outgoing;

        /// <summary>Extracted first, then by location.</summary>
        public readonly IReadOnlyList<GraphEdge> Edges;

        public SubgraphLink(SubgraphNode other, string relation, bool outgoing, IReadOnlyList<GraphEdge> edges)
        {
            Other = other;
            Relation = relation;
            Outgoing = outgoing;
            Edges = edges;
        }

        public bool Extracted => Edges.Any(e => e.Confidence == Confidence.Extracted);
    }
}
