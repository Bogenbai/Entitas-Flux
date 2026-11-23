using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace Entitas.Graph.Output
{
    /// <summary>An edge identity — (source, target, relation) — that one side has and the other does not.</summary>
    public sealed class EdgeChange
    {
        public bool Added;
        public string Source = string.Empty;
        public string Target = string.Empty;
        public string Relation = string.Empty;

        /// <summary>The edges behind the identity, from the side that has it: their via, confidence and through describe it.</summary>
        public IReadOnlyList<GraphEdge> Edges = Array.Empty<GraphEdge>();
    }

    /// <summary>A system or feature registered by other features, or at another place among its siblings.</summary>
    public sealed class RegistrationChange
    {
        public string Node = string.Empty;
        public IReadOnlyList<Registration> Before = Array.Empty<Registration>();
        public IReadOnlyList<Registration> After = Array.Empty<Registration>();
    }

    /// <summary>A GRAPH_REPORT.md finding that appeared or went away.</summary>
    public sealed class FindingChange
    {
        public bool Appeared;
        public string Section = string.Empty;
        public string Name = string.Empty;
    }

    /// <summary>
    /// What changed in the data flow between two graphs of a project. A file diff says "this
    /// system changed"; this says "three systems stopped reading FurnitureAddedToCafeEvent and
    /// read ShopPurchaseEvent instead", which is what a reviewer of a branch needs to know.
    ///
    /// Nodes are compared by id and edges by (source, target, relation) alone. Via, file, line
    /// and count move with every edit — a line added above an access, a read moved into a
    /// helper — and would bury the few changes that matter.
    ///
    /// Registrations are compared by order, not number: inserting a system renumbers everything
    /// after it without changing what runs before what. Only systems whose order relative to
    /// their siblings changed (outside the longest common subsequence of the feature's children)
    /// or whose features changed are reported as moved; the merely renumbered are counted.
    /// </summary>
    public sealed class GraphDiff
    {
        /// <summary>Edge lines per component, and names per summary line, while the answer is capped.</summary>
        const int LinesPerComponent = 25, NamesInSummary = 10;

        const string Minus = "−";

        static readonly string[] RelationOrder =
            { Relation.Creates, Relation.Reads, Relation.Reacts, Relation.Filters, Relation.Removes, Relation.Destroys, Relation.Contains };

        readonly EcsGraph _old;
        readonly EcsGraph _new;
        readonly HashSet<string> _oldIds;
        readonly HashSet<string> _newIds;

        public IReadOnlyList<GraphNode> AddedNodes { get; private set; } = Array.Empty<GraphNode>();
        public IReadOnlyList<GraphNode> RemovedNodes { get; private set; } = Array.Empty<GraphNode>();
        public IReadOnlyList<EdgeChange> EdgeChanges { get; private set; } = Array.Empty<EdgeChange>();
        public IReadOnlyList<RegistrationChange> Moves { get; private set; } = Array.Empty<RegistrationChange>();

        /// <summary>Per feature id: children whose number changed while their order among the siblings did not.</summary>
        public IReadOnlyDictionary<string, int> Renumbered { get; private set; } = new Dictionary<string, int>();

        public IReadOnlyList<FindingChange> Findings { get; private set; } = Array.Empty<FindingChange>();

        public bool IsEmpty =>
            AddedNodes.Count == 0 && RemovedNodes.Count == 0 && EdgeChanges.Count == 0 && Moves.Count == 0 && Renumbered.Count == 0 && Findings.Count == 0;

        GraphDiff(EcsGraph old, EcsGraph @new)
        {
            _old = old;
            _new = @new;
            _oldIds = new HashSet<string>(old.Nodes.Select(n => n.Id), StringComparer.Ordinal);
            _newIds = new HashSet<string>(@new.Nodes.Select(n => n.Id), StringComparer.Ordinal);
        }

        public static GraphDiff Compare(EcsGraph old, EcsGraph @new)
        {
            var diff = new GraphDiff(old, @new);
            diff.AddedNodes = @new.Nodes.Where(n => !diff._oldIds.Contains(n.Id)).OrderBy(n => n.Label, StringComparer.Ordinal).ThenBy(n => n.Id, StringComparer.Ordinal).ToList();
            diff.RemovedNodes = old.Nodes.Where(n => !diff._newIds.Contains(n.Id)).OrderBy(n => n.Label, StringComparer.Ordinal).ThenBy(n => n.Id, StringComparer.Ordinal).ToList();

            var before = ByIdentity(old);
            var after = ByIdentity(@new);
            diff.EdgeChanges = after.Where(pair => !before.ContainsKey(pair.Key)).Select(pair => Change(pair.Value, added: true))
                .Concat(before.Where(pair => !after.ContainsKey(pair.Key)).Select(pair => Change(pair.Value, added: false)))
                .ToList();

            diff.CompareRegistrations();
            diff.CompareFindings();
            return diff;
        }

        static Dictionary<string, List<GraphEdge>> ByIdentity(EcsGraph graph) =>
            graph.Edges.GroupBy(e => e.Source + "\u0001" + e.Target + "\u0001" + e.Relation, StringComparer.Ordinal)
                .ToDictionary(g => g.Key, g => g.ToList(), StringComparer.Ordinal);

        static EdgeChange Change(List<GraphEdge> edges, bool added) => new EdgeChange
        {
            Added = added, Source = edges[0].Source, Target = edges[0].Target, Relation = edges[0].Relation, Edges = edges
        };

        // -- registrations ------------------------------------------------------------------

        void CompareRegistrations()
        {
            var before = Registrations(_old);
            var after = Registrations(_new);
            var moved = new HashSet<string>(StringComparer.Ordinal);
            var renumbered = new List<(string Feature, string Child)>();

            foreach (var feature in Children(_old).Keys.Intersect(Children(_new).Keys, StringComparer.Ordinal))
            {
                var a = Children(_old)[feature];
                var b = Children(_new)[feature];
                var common = new HashSet<string>(a.Intersect(b, StringComparer.Ordinal), StringComparer.Ordinal);
                var stable = Stable(a.Where(common.Contains).ToList(), b.Where(common.Contains).ToList());
                foreach (var child in a.Where(common.Contains))
                {
                    if (!stable.Contains(child)) moved.Add(child);
                    else if (Index(before[child], feature) != Index(after[child], feature)) renumbered.Add((feature, child));
                }
            }

            // Registered by other features (or by none any more, or for the first time).
            foreach (var child in before.Keys.Union(after.Keys, StringComparer.Ordinal).Where(id => _oldIds.Contains(id) && _newIds.Contains(id)))
            {
                var was = Features(before, child);
                var now = Features(after, child);
                if (!was.SetEquals(now)) moved.Add(child);
            }

            Moves = moved
                .Select(child => new RegistrationChange
                {
                    Node = child,
                    Before = before.TryGetValue(child, out var b) ? b : Array.Empty<Registration>(),
                    After = after.TryGetValue(child, out var a) ? a : Array.Empty<Registration>()
                })
                .OrderBy(m => Label(m.Node), StringComparer.Ordinal)
                .ToList();
            Renumbered = renumbered.Where(r => !moved.Contains(r.Child))
                .GroupBy(r => r.Feature)
                .ToDictionary(g => g.Key, g => g.Count(), StringComparer.Ordinal);

            static int Index(List<Registration> registrations, string feature) => registrations.First(r => r.Feature == feature).Index;

            static HashSet<string> Features(Dictionary<string, List<Registration>> registrations, string child) =>
                new HashSet<string>(registrations.TryGetValue(child, out var list) ? list.Select(r => r.Feature) : Array.Empty<string>(), StringComparer.Ordinal);
        }

        readonly Dictionary<EcsGraph, Dictionary<string, List<string>>> _children = new Dictionary<EcsGraph, Dictionary<string, List<string>>>();

        /// <summary>Per feature, the systems and features it registers, in Add(...) order.</summary>
        Dictionary<string, List<string>> Children(EcsGraph graph)
        {
            if (!_children.TryGetValue(graph, out var children))
                _children[graph] = children = graph.Edges.Where(e => e.Relation == Relation.Contains)
                    .GroupBy(e => e.Source, StringComparer.Ordinal)
                    .ToDictionary(g => g.Key, g => g.OrderBy(e => e.Order).Select(e => e.Target).Distinct(StringComparer.Ordinal).ToList(), StringComparer.Ordinal);
            return children;
        }

        /// <summary>Per system or feature, where it is registered — from the contains edges, which a saved graph always has.</summary>
        static Dictionary<string, List<Registration>> Registrations(EcsGraph graph) =>
            graph.Edges.Where(e => e.Relation == Relation.Contains)
                .GroupBy(e => e.Target, StringComparer.Ordinal)
                .ToDictionary(
                    g => g.Key,
                    g => g.Select(e => new Registration { Feature = e.Source, Index = e.Order })
                        .OrderBy(r => r.Feature, StringComparer.Ordinal).ThenBy(r => r.Index).ToList(),
                    StringComparer.Ordinal);

        /// <summary>The longest common subsequence of two orders: the children that kept their order relative to each other.</summary>
        static HashSet<string> Stable(IReadOnlyList<string> before, IReadOnlyList<string> after)
        {
            var lengths = new int[before.Count + 1, after.Count + 1];
            for (var i = before.Count - 1; i >= 0; i--)
                for (var j = after.Count - 1; j >= 0; j--)
                    lengths[i, j] = before[i] == after[j] ? lengths[i + 1, j + 1] + 1 : Math.Max(lengths[i + 1, j], lengths[i, j + 1]);

            var stable = new HashSet<string>(StringComparer.Ordinal);
            for (int i = 0, j = 0; i < before.Count && j < after.Count;)
            {
                if (before[i] == after[j])
                {
                    stable.Add(before[i]);
                    i++;
                    j++;
                }
                else if (lengths[i + 1, j] >= lengths[i, j + 1]) i++;
                else j++;
            }
            return stable;
        }

        // -- findings ---------------------------------------------------------------------------

        /// <summary>
        /// GRAPH_REPORT.md's findings, compared per section by the backticked name each bullet
        /// starts with — the rest of a bullet (who creates it, file:line) moves with edits that
        /// are already reported as edge changes.
        /// </summary>
        void CompareFindings()
        {
            var before = ReadFindings(_old);
            var after = ReadFindings(_new);
            var changes = new List<FindingChange>();
            foreach (var section in after.Select(s => s.Title).Concat(before.Select(s => s.Title)).Distinct())
            {
                var was = before.FirstOrDefault(s => s.Title == section).Names ?? new HashSet<string>();
                var now = after.FirstOrDefault(s => s.Title == section).Names ?? new HashSet<string>();
                changes.AddRange(now.Where(n => !was.Contains(n)).OrderBy(n => n, StringComparer.Ordinal).Select(n => new FindingChange { Appeared = true, Section = section, Name = n }));
                changes.AddRange(was.Where(n => !now.Contains(n)).OrderBy(n => n, StringComparer.Ordinal).Select(n => new FindingChange { Appeared = false, Section = section, Name = n }));
            }
            Findings = changes;
        }

        static List<(string Title, HashSet<string> Names)> ReadFindings(EcsGraph graph)
        {
            var report = GraphReport.Build(graph, itemsPerSection: int.MaxValue);
            var sections = new List<(string Title, HashSet<string> Names)>();
            var start = report.IndexOf("\n## Findings", StringComparison.Ordinal);
            if (start < 0) return sections;

            foreach (var line in report.Substring(start).Split('\n'))
            {
                if (line.StartsWith("### ", StringComparison.Ordinal))
                {
                    // "### Components nobody reads — 32": the count is not part of the name.
                    var title = line.Substring(4).TrimEnd('\r');
                    var dash = title.LastIndexOf(" — ", StringComparison.Ordinal);
                    sections.Add((dash > 0 ? title.Substring(0, dash) : title, new HashSet<string>(StringComparer.Ordinal)));
                }
                else if (sections.Count > 0 && line.StartsWith("- `", StringComparison.Ordinal))
                {
                    var end = line.IndexOf('`', 3);
                    if (end > 3) sections[sections.Count - 1].Names.Add(line.Substring(3, end - 3));
                }
            }
            return sections;
        }

        // -- output -----------------------------------------------------------------------------

        /// <summary>
        /// The changes as text, capped at <paramref name="budget"/> tokens like every other answer:
        /// counts first, then per component the edges it gained and lost (most changes first),
        /// then systems added, removed and moved, then findings. A cut answer adds the names of
        /// everything changed right after the counts, so nothing is invisible.
        /// </summary>
        public string Render(string oldLabel, string newLabel, int budget = GraphQueries.DefaultBudget)
        {
            var capped = budget > 0;
            var head = new List<string> { $"old: {oldLabel}{Commit(_old)}", $"new: {newLabel}{Commit(_new)}" };
            var renderer = new GraphQueries(_new, budget);
            if (IsEmpty)
            {
                head.Add("No changes in the data flow: the same components, systems, features, edges, registrations and findings.");
                return renderer.Render(head, Array.Empty<string>, Array.Empty<string>(), "changes");
            }

            var components = ComponentChanges();
            var renumbered = Renumbered.Values.Sum();
            var kinds = new[] { (NodeKind.Component, "components"), (NodeKind.System, "systems"), (NodeKind.Feature, "features"), (NodeKind.Type, "other types") }
                .Select(k => (Noun: k.Item2, Change: PlusMinus(AddedNodes.Count(n => n.Kind == k.Item1), RemovedNodes.Count(n => n.Kind == k.Item1))))
                .Where(k => k.Change.Length > 0)
                .Select(k => $"{k.Noun} {k.Change}")
                .ToList();
            head.Add("nodes: " + (kinds.Count > 0 ? string.Join(" · ", kinds) : "none added or removed"));
            var relations = EdgeChanges.GroupBy(c => c.Relation).OrderBy(g => Array.IndexOf(RelationOrder, g.Key))
                .Select(g => $"{g.Key} {PlusMinus(g.Count(c => c.Added), g.Count(c => !c.Added))}").ToList();
            head.Add("edges: " + (relations.Count > 0 ? string.Join(" · ", relations) : "none added or removed"));
            var findings = PlusMinus(Findings.Count(f => f.Appeared), Findings.Count(f => !f.Appeared));
            head.Add($"{components.Count} components changed · {Moves.Count} registrations moved{(renumbered > 0 ? $" ({renumbered} renumbered)" : string.Empty)} · " +
                     $"findings {(findings.Length > 0 ? findings : "unchanged")}");

            var entries = new List<string>();
            var title = $"\nComponents ({components.Count}):\n";
            foreach (var (component, changes) in components)
            {
                var entry = new StringBuilder(title);
                title = string.Empty;
                var status = !_oldIds.Contains(component) ? "  (new component)" : !_newIds.Contains(component) ? "  (removed component)" : string.Empty;
                entry.AppendLine($"  {Label(component)}  {PlusMinus(changes.Count(c => c.Added), changes.Count(c => !c.Added))}{status}".TrimEnd());
                var shown = capped ? Math.Min(changes.Count, LinesPerComponent) : changes.Count;
                foreach (var change in changes.Take(shown))
                    entry.AppendLine("    " + EdgeLine(change));
                if (shown < changes.Count)
                    entry.AppendLine($"    … {changes.Count - shown} more");
                entries.Add(entry.ToString());
            }

            var addedNodes = NonComponents(AddedNodes);
            var removedNodes = NonComponents(RemovedNodes);
            var registeredNow = Registrations(_new);
            var registeredBefore = Registrations(_old);
            title = $"\nSystems ({addedNodes.Count} added, {removedNodes.Count} removed, {Moves.Count} moved):\n";
            foreach (var node in addedNodes)
            {
                // A query or factory is never registered; saying so on every one would be noise.
                var where = node.Kind == NodeKind.Type ? string.Empty : "  " + Where(registeredNow, node.Id);
                entries.Add(title + $"  + {node.Label}{KindSuffix(node)}{where}\n");
                title = string.Empty;
            }
            foreach (var node in removedNodes)
            {
                var where = node.Kind == NodeKind.Type ? string.Empty : $"  (was {Where(registeredBefore, node.Id)})";
                entries.Add(title + $"  {Minus} {node.Label}{KindSuffix(node)}{where}\n");
                title = string.Empty;
            }
            foreach (var move in Moves)
            {
                entries.Add(title + $"  ~ {Label(move.Node)}{KindSuffix(_new.Node(move.Node))}: {Moved(move)}\n");
                title = string.Empty;
            }
            if (renumbered > 0)
                entries.Add(title + $"  {renumbered} more renumbered, their order among the others unchanged: " +
                            string.Join(", ", Renumbered.OrderByDescending(p => p.Value).ThenBy(p => Label(p.Key), StringComparer.Ordinal).Select(p => $"{Label(p.Key)} {p.Value}")) + "\n");

            title = $"\nFindings ({PlusMinus(Findings.Count(f => f.Appeared), Findings.Count(f => !f.Appeared))}):\n";
            foreach (var finding in Findings.OrderBy(f => f.Appeared ? 0 : 1))
            {
                entries.Add(title + $"  {(finding.Appeared ? "+" : Minus)} {finding.Section}: {finding.Name}\n");
                title = string.Empty;
            }

            return renderer.Render(head, () => Summary(components), entries, "changes");
        }

        List<(string Component, List<EdgeChange> Changes)> ComponentChanges()
        {
            var byComponent = EdgeChanges.Where(c => c.Relation != Relation.Contains)
                .GroupBy(c => c.Target, StringComparer.Ordinal)
                .ToDictionary(g => g.Key, g => g.ToList(), StringComparer.Ordinal);
            foreach (var node in AddedNodes.Concat(RemovedNodes).Where(n => n.Kind == NodeKind.Component && !byComponent.ContainsKey(n.Id)))
                byComponent[node.Id] = new List<EdgeChange>();

            return byComponent
                .Select(pair => (pair.Key, pair.Value
                    .OrderBy(c => Array.IndexOf(RelationOrder, c.Relation))
                    .ThenBy(c => c.Added ? 1 : 0)
                    .ThenBy(c => Label(c.Source), StringComparer.Ordinal)
                    .ToList()))
                .OrderByDescending(pair => pair.Item2.Count)
                .ThenBy(pair => Label(pair.Key), StringComparer.Ordinal)
                .ToList();
        }

        IEnumerable<string> Summary(List<(string Component, List<EdgeChange> Changes)> components)
        {
            if (components.Count > 0)
                yield return "  components, most changes first: " + Names(components.Select(c =>
                    $"{Label(c.Component)} {PlusMinus(c.Changes.Count(x => x.Added), c.Changes.Count(x => !x.Added))}".TrimEnd()));

            var systems = new List<string>();
            var added = NonComponents(AddedNodes);
            var removed = NonComponents(RemovedNodes);
            if (added.Count > 0) systems.Add("added " + Names(added.Select(n => n.Label + KindSuffix(n))));
            if (removed.Count > 0) systems.Add("removed " + Names(removed.Select(n => n.Label + KindSuffix(n))));
            if (Moves.Count > 0) systems.Add("moved " + Names(Moves.Select(m => Label(m.Node))));
            if (systems.Count > 0)
                yield return "  systems: " + string.Join(" · ", systems);

            if (Findings.Count > 0)
                yield return "  findings: " + Names(Findings.OrderBy(f => f.Appeared ? 0 : 1).Select(f => $"{(f.Appeared ? "+" : Minus)}{f.Name} ({f.Section})"));
        }

        static string Names(IEnumerable<string> names)
        {
            var list = names.ToList();
            return string.Join(", ", list.Take(NamesInSummary)) + (list.Count > NamesInSummary ? $", … {list.Count - NamesInSummary} more" : string.Empty);
        }

        string EdgeLine(EdgeChange change)
        {
            var vias = string.Join(", ", change.Edges
                .Select(e => e.Via + (e.Confidence == Confidence.Inferred ? "*" : string.Empty))
                .Where(v => v.Length > 0)
                .Distinct()
                .OrderBy(v => v, StringComparer.Ordinal));
            // Like lifecycle: what an inferred-only edge went through, so the reviewer knows where to look.
            var throughs = change.Edges.All(e => e.Confidence == Confidence.Inferred)
                ? change.Edges.Select(e => e.Through).Where(t => t != null).Distinct().OrderBy(t => t, StringComparer.Ordinal).Take(2).ToList()
                : new List<string?>();
            var detail = vias + (throughs.Count > 0 ? $", through {string.Join("; ", throughs)}" : string.Empty);

            var source = _new.Node(change.Source) ?? _old.Node(change.Source);
            var status = source == null ? string.Empty
                : !_oldIds.Contains(change.Source) ? $" — new {source.Kind}"
                : !_newIds.Contains(change.Source) ? $" — {source.Kind} removed"
                : string.Empty;
            return $"{(change.Added ? "+" : Minus)} {GraphQueries.Passive(change.Relation)} {Label(change.Source)}{(detail.Length > 0 ? $" ({detail})" : string.Empty)}{status}";
        }

        string Moved(RegistrationChange move)
        {
            // The common case, one feature before and after: "FurnitureFeature #4 → #5".
            if (move.Before.Count == 1 && move.After.Count == 1 && move.Before[0].Feature == move.After[0].Feature)
                return $"{Label(move.Before[0].Feature)} #{move.Before[0].Index} → #{move.After[0].Index}";
            return $"{Where(move.Before)} → {Where(move.After)}";
        }

        string Where(Dictionary<string, List<Registration>> registrations, string id) =>
            Where(registrations.TryGetValue(id, out var list) ? list : Array.Empty<Registration>());

        string Where(IReadOnlyList<Registration> registrations) =>
            registrations.Count == 0 ? "not registered" : string.Join(", ", registrations.Select(r => $"{Label(r.Feature)} #{r.Index}"));

        static string PlusMinus(int plus, int minus) =>
            string.Join(" ", new[] { plus > 0 ? "+" + plus : null, minus > 0 ? Minus + minus : null }.Where(s => s != null));

        /// <summary>Systems first, then features, then other types: the order a reviewer cares about them in.</summary>
        static List<GraphNode> NonComponents(IEnumerable<GraphNode> nodes) =>
            nodes.Where(n => n.Kind != NodeKind.Component)
                .OrderBy(n => n.Kind == NodeKind.System ? 0 : n.Kind == NodeKind.Feature ? 1 : 2)
                .ThenBy(n => n.Label, StringComparer.Ordinal)
                .ToList();

        static string KindSuffix(GraphNode? node) =>node == null || node.Kind == NodeKind.System ? string.Empty : $" ({node.Kind})";

        static string Commit(EcsGraph graph)
        {
            var commit = graph.Metadata.BuiltAtCommit;
            return string.IsNullOrEmpty(commit) ? string.Empty : $" (commit {commit!.Substring(0, Math.Min(10, commit.Length))})";
        }

        string Label(string id) => _new.Node(id)?.Label ?? _old.Node(id)?.Label ?? id;
    }
}
