using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace Entitas.Graph.Output
{
    /// <summary>
    /// GRAPH_REPORT.md: the project at a glance, then what looks wrong. The findings are
    /// structural (a component nobody reads, one read but never created) plus a few checks of
    /// common Entitas conventions — requests are destroyed by their handler, events are not
    /// destroyed by hand, one-frame flags are cleared — which recognise requests and events
    /// by name suffix (configurable), never by the names of a particular project.
    /// </summary>
    public static class GraphReport
    {
        /// <summary>Findings listed per section; the rest is counted. The diff passes no limit, so it compares every finding.</summary>
        public const int ItemsPerSection = 60;

        public static string Build(EcsGraph graph, int itemsPerSection = ItemsPerSection)
        {
            var sb = new StringBuilder();
            var components = graph.Nodes.Where(n => n.Kind == NodeKind.Component && !n.Generated).OrderBy(n => n.Label, StringComparer.Ordinal).ToList();
            var systems = graph.Nodes.Where(n => n.Kind == NodeKind.System).ToList();
            var features = graph.Nodes.Where(n => n.Kind == NodeKind.Feature).ToList();
            var incoming = graph.Edges.GroupBy(e => e.Target).ToDictionary(g => g.Key, g => g.ToList(), StringComparer.Ordinal);
            List<GraphEdge> In(GraphNode n) => incoming.TryGetValue(n.Id, out var list) ? list : new List<GraphEdge>();
            bool Has(GraphNode n, params string[] relations) => In(n).Any(e => relations.Contains(e.Relation));
            string Name(string id) => graph.Node(id)?.Label ?? id;

            sb.AppendLine($"# ECS graph — {graph.Metadata.Project}");
            sb.AppendLine();
            sb.AppendLine($"Built by entitas-graph {graph.Metadata.ToolVersion} from {graph.Metadata.SourceFiles} source files in {graph.Metadata.BuildMilliseconds} ms" +
                          (graph.Metadata.BuiltAtCommit != null ? $", at commit `{graph.Metadata.BuiltAtCommit.Substring(0, Math.Min(10, graph.Metadata.BuiltAtCommit.Length))}`." : "."));
            sb.AppendLine();

            sb.AppendLine("## Summary");
            sb.AppendLine();
            sb.AppendLine($"- **{components.Count}** components, **{systems.Count}** systems, **{features.Count}** features, " +
                          $"**{graph.Nodes.Count(n => n.Kind == NodeKind.Type)}** other types touching components (queries, factories, services…)");
            var relationCounts = graph.Edges.GroupBy(e => e.Relation).OrderBy(g => g.Key, StringComparer.Ordinal).Select(g => $"{g.Key} {g.Count()}");
            sb.AppendLine($"- {graph.Edges.Count} edges: {string.Join(", ", relationCounts)}");
            var extracted = graph.Edges.Count(e => e.Confidence == Confidence.Extracted);
            sb.AppendLine($"- {Percent(extracted, graph.Edges.Count)} written in the type itself (EXTRACTED), {Percent(graph.Edges.Count - extracted, graph.Edges.Count)} through a query, factory or entity shape (INFERRED)");
            foreach (var root in features.Where(f => f.Root).OrderBy(f => f.Label, StringComparer.Ordinal))
                sb.AppendLine($"- root feature **{root.Label}** runs {systems.Count(s => s.Placements.Any(p => p.Root == root.Id))} systems");
            sb.AppendLine($"- messages: {Messages(components, "request", graph.Metadata.RequestSuffix)}; {Messages(components, "event", graph.Metadata.EventSuffix)}");

            // The gap is on purpose, and only the suffix closes it: said here so that nobody takes
            // it for a bug to fix by loosening the structure, or turns the suffix off.
            var byNameOnly = components.Where(c => c.Role == "request" && c.Message != "request").Select(c => $"`{c.Label}`").ToList();
            if (byNameOnly.Count > 0)
                sb.AppendLine($"  - {byNameOnly.Count} of the requests are found by name only: no loop handling them also destroys or removes them every time " +
                              "it runs — they wait for something, are taken off elsewhere, or nobody handles them. The structure leaves those to the " +
                              "name on purpose (a hint that lives until a window opens looks the same), so keep `--request-suffix`: " +
                              string.Join(", ", byNameOnly.Take(5)) + (byNameOnly.Count > 5 ? $" and {byNameOnly.Count - 5} more" : string.Empty));
            sb.AppendLine();

            sb.AppendLine("## Most connected components");
            sb.AppendLine();
            foreach (var component in components.OrderByDescending(c => In(c).Select(e => e.Source).Distinct().Count()).ThenBy(c => c.Label).Take(12))
            {
                var edges = In(component);
                sb.AppendLine($"1. `{component.Label}` — {edges.Select(e => e.Source).Distinct().Count()} types: " +
                              $"{Distinct(edges, Relation.Creates)} create, {Distinct(edges, Relation.Reads, Relation.Reacts)} read, {Distinct(edges, Relation.Filters)} filter, " +
                              $"{Distinct(edges, Relation.Removes)} remove, {Distinct(edges, Relation.Destroys)} destroy");
            }
            sb.AppendLine();

            sb.AppendLine("## Findings");
            sb.AppendLine();
            Section(sb, itemsPerSection, "Components nobody reads",
                "Created somewhere, but no group, accessor or [Watched] reaction ever looks at them.",
                components.Where(c => Has(c, Relation.Creates) && !Has(c, Relation.Reads, Relation.Reacts, Relation.Filters)).Select(c => Line(c, In(c), Relation.Creates, "created by", graph)));

            Section(sb, itemsPerSection, "Components read but never created",
                "Something waits for them, no code makes them — created from data (component lists in prefabs or configs added through AddComponent at runtime), in another assembly, or dead.",
                components.Where(c => Has(c, Relation.Reads, Relation.Reacts) && !Has(c, Relation.Creates) && !c.Attributes.Contains("Unique")).Select(c => Line(c, In(c), Relation.Reads, "read by", graph)));

            // These four go by the naming convention, not by structure: a request without a
            // handler is never consumed, and an event without a consumer is read by nobody, so
            // what happens to their entities cannot tell them from state. Only a name can.
            var requestSuffix = graph.Metadata.RequestSuffix;
            var eventSuffix = graph.Metadata.EventSuffix;
            Section(sb, itemsPerSection, $"Requests without a handler (`*{requestSuffix}`)",
                "Created, but no system picks them up. These four sections go by name (`--request-suffix`, `--event-suffix`), not by structure: " +
                "a request nobody handles is never consumed and an event nobody reads is read by nobody, so only a name tells them from any other component.",
                components.Where(c => c.Role == "request" && Has(c, Relation.Creates) && !Has(c, Relation.Reads)).Select(c => Line(c, In(c), Relation.Creates, "created by", graph)));

            Section(sb, itemsPerSection, $"Requests nobody destroys or removes (`*{requestSuffix}`)",
                "Handled, but neither destroyed nor removed — a handler should get rid of the request it processed, or it is processed again next frame. By name (`--request-suffix`).",
                components.Where(c => c.Role == "request" && Has(c, Relation.Reads) && !Has(c, Relation.Destroys, Relation.Removes)).Select(c => Line(c, In(c), Relation.Reads, "handled by", graph)));

            Section(sb, itemsPerSection, $"Events without a consumer (`*{eventSuffix}`)",
                "Created, but nothing reads them. By name (`--event-suffix`).",
                components.Where(c => c.Role == "event" && Has(c, Relation.Creates) && !Has(c, Relation.Reads, Relation.Reacts)).Select(c => Line(c, In(c), Relation.Creates, "created by", graph)));

            Section(sb, itemsPerSection, $"Events destroyed by hand (`*{eventSuffix}`)",
                "A system destroys entities of a group that names the event itself; events are usually destroyed once, generically, after everyone had a chance to read them. By name (`--event-suffix`).",
                components.Where(c => c.Role == "event")
                    .SelectMany(c => In(c).Where(e => e.Relation == Relation.Destroys && e.Confidence == Confidence.Extracted)
                        .Select(e => $"- `{c.Label}` destroyed by `{Name(e.Source)}` at {e.SourceFile}:{e.Line}")));

            Section(sb, itemsPerSection, "[Watched] components nobody reacts to",
                "Each one costs a generated cleanup system; nothing of yours reads its Changed marker.",
                components.Where(c => c.Attributes.Contains("Watched") &&
                                      !In(c).Any(e => e.Relation == Relation.Reacts && graph.Node(e.Source)?.Generated == false))
                    .Select(c => $"- `{c.Label}` {c.SourceFile}:{c.Line}"));

            var neverCleared = components
                .Where(c => c.Flag && !c.Attributes.Contains("Unique") && !c.Attributes.Any(a => a.StartsWith("Cleanup", StringComparison.Ordinal)) &&
                            In(c).Any(e => e.Relation == Relation.Creates && e.Entity == "group") && Has(c, Relation.Reads) &&
                            !Has(c, Relation.Removes, Relation.Destroys))
                .ToList();

            // A system that sets a flag on its group's entities and leaves the flag out of the
            // group with NoneOf marks each entity once and then skips it: a "done" marker
            // (RoomExpanded, GiftOpened), never cleared by design. Mixed in with the flags that
            // really stick, they made the list read as noise. The NoneOf must be the setter's
            // own (EXTRACTED, not through a query), and every setter on existing entities must
            // have one — a second setter without the guard keeps the flag in the first list.
            //
            // Not when the setter reacts to a [Watched] change: a change comes again (a customer
            // sits down a second time), and the NoneOf then freezes the first outcome — the
            // stale-flag bug this list is for (SatAtTable: set on the first sitting, still set
            // after the customer moved to a chair with no table).
            //
            // A setter whose loops run over a group requiring a transient component marks each
            // entity once too, without a NoneOf: it only sees the entity while the entity carries
            // that component (FloorRestored is set on AllOf(…, JustCreated), and
            // UnmarkJustCreatedEntitiesSystem takes JustCreated off every entity). Only the builder
            // sees groups, so it records that on the setter's edge (only_while). The same [Watched]
            // exception applies: a guard that lets the setter run once freezes the first outcome
            // just the same.
            var reacting = new HashSet<string>(graph.Edges.Where(e => e.Relation == Relation.Reacts && e.Confidence == Confidence.Extracted).Select(e => e.Source), StringComparer.Ordinal);
            List<string> Setters(GraphNode c) => In(c).Where(e => e.Relation == Relation.Creates && e.Entity == "group").Select(e => e.Source).Distinct().ToList();
            string? OnlyWhile(GraphNode c, string setter)
            {
                var writes = In(c).Where(e => e.Source == setter && e.Relation == Relation.Creates && e.Entity == "group").ToList();
                return writes.All(e => e.OnlyWhile != null) ? writes.Select(e => e.OnlyWhile!).OrderBy(g => g, StringComparer.Ordinal).First() : null;
            }
            bool SetOnce(GraphNode c) =>
                Setters(c).All(s => !reacting.Contains(s) &&
                                    (In(c).Any(e => e.Source == s && e.Relation == Relation.Filters && e.Confidence == Confidence.Extracted) ||
                                     OnlyWhile(c, s) != null));

            // Why a flag counts as set once when no NoneOf says so: who sees entities only while
            // they carry what, and what takes that away. The line has to stand on its own.
            string? OnlyWhileReason(GraphNode c)
            {
                var setters = Setters(c);
                var guarded = setters.Select(s => (Setter: s, Guard: OnlyWhile(c, s))).Where(g => g.Guard != null).ToList();
                if (guarded.Count == 0)
                    return null;

                return string.Join("; ", guarded.GroupBy(g => g.Guard!).OrderBy(g => g.Key, StringComparer.Ordinal).Select(g =>
                {
                    var who = setters.Count == 1
                        ? "the setter only sees"
                        : string.Join(", ", g.Select(s => $"`{Name(s.Setter)}`").OrderBy(n => n, StringComparer.Ordinal)) + (g.Count() == 1 ? " only sees" : " only see");
                    return $"{who} entities while they have `{Name(g.Key)}`, {TakenAwayBy(g.Key)}";
                }));
            }

            string TakenAwayBy(string transientId)
            {
                var transient = graph.Node(transientId);
                var cleanup = transient?.Attributes.FirstOrDefault(a => a.StartsWith("Cleanup", StringComparison.Ordinal));
                if (cleanup != null)
                    return cleanup == "Cleanup(DestroyEntity)" ? "whose entities [Cleanup] destroys" : "which [Cleanup] removes";
                var removers = transient?.TransientRemovedBy.Select(r => $"`{Name(r)}`").ToList() ?? new List<string>();
                return removers.Count == 0
                    ? "which no entity keeps"
                    : $"which {string.Join(" and ", removers)} remove{(removers.Count == 1 ? "s" : string.Empty)}";
            }

            Section(sb, itemsPerSection, "State flags that are never cleared",
                "Flags set on existing entities (inside a foreach over a group) and read, but never removed, destroyed or [Cleanup] — " +
                "once set they stay set, so every later frame sees them. Flags set while creating an entity (identity markers) are not listed, " +
                "nor are flags their setter excludes with NoneOf or sets only while the entity carries a transient component (next section) — " +
                "unless the setter reacts to a [Watched] change, which comes again while the flag stays from the first time.",
                neverCleared.Where(c => !SetOnce(c)).Select(c => Line(c, In(c), Relation.Creates, "set by", graph)));

            Section(sb, itemsPerSection, "Flags set once on purpose? (the setter filters them out with NoneOf, or only sees entities while they carry a transient component)",
                "Never cleared either, but every system setting them on existing entities either excludes them from its own group with NoneOf, " +
                "or loops over a group that requires a transient component — one with [Cleanup], or one a system takes off every entity holding it, such as a one-frame \"just created\" marker — " +
                "so each entity is marked once and then left alone: the usual shape of a \"done\" marker. Usually deliberate; " +
                "worth a look only if something expects the flag to go away again.",
                neverCleared.Where(SetOnce).Select(c => Line(c, In(c), Relation.Creates, "set by", graph, OnlyWhileReason(c))));

            Section(sb, itemsPerSection, "Systems not registered in any feature",
                "They exist but nothing adds them, so they never run (or are added outside a feature). A generated feature listed here " +
                "(`<Context>CleanupSystems`, `<Context>WatchedCleanupSystems`, `<Context>EventSystems`) means its [Cleanup], [Watched] or [Event] systems never run.",
                systems.Where(s => s.RegisteredIn.Count == 0 && !s.Generated)
                    .Concat(features.Where(f => f.Generated && f.RegisteredIn.Count == 0))
                    .OrderBy(s => s.Label)
                    .Select(s => $"- `{s.Label}`{(s.Kind == NodeKind.Feature ? " (generated feature)" : string.Empty)} {s.SourceFile}:{s.Line}"));

            Section(sb, itemsPerSection, "Destroy() calls not traced to a group",
                "The destroyed entity does not come from a foreach over a group, so what it destroys is unknown here.",
                graph.Metadata.UnresolvedDestroys.OrderBy(u => u.File).ThenBy(u => u.Line).Select(u => $"- `{Name(u.Source)}` at {u.File}:{u.Line}"));

            return sb.ToString();
        }

        static void Section(StringBuilder sb, int limit, string title, string explanation, IEnumerable<string> lines)
        {
            var items = lines.ToList();
            sb.AppendLine($"### {title} — {items.Count}");
            sb.AppendLine();
            sb.AppendLine(explanation);
            sb.AppendLine();
            if (items.Count == 0)
            {
                sb.AppendLine("None.");
            }
            else
            {
                foreach (var line in items.Take(limit))
                    sb.AppendLine(line);
                if (items.Count > limit)
                    sb.AppendLine($"- … and {items.Count - limit} more");
            }
            sb.AppendLine();
        }

        static string Line(GraphNode component, List<GraphEdge> edges, string relation, string verb, EcsGraph graph, string? reason = null)
        {
            var who = edges.Where(e => e.Relation == relation).Select(e => graph.Node(e.Source)?.Label ?? e.Source).Distinct().OrderBy(n => n, StringComparer.Ordinal).Take(4).ToList();
            return $"- `{component.Label}` {(who.Count > 0 ? $"{verb} {string.Join(", ", who.Select(w => $"`{w}`"))}" : string.Empty)}" +
                   $"{(reason != null ? " — " + reason : string.Empty)} — {component.SourceFile}:{component.Line}";
        }

        static int Distinct(IEnumerable<GraphEdge> edges, params string[] relations) =>
            edges.Where(e => relations.Contains(e.Relation)).Select(e => e.Source).Distinct().Count();

        /// <summary>
        /// "88 requests by name (`*Request`), 72 by structure, 70 of them both": how far the
        /// project's naming and what its entities go through agree. The commands follow the name
        /// where it says, the structure elsewhere; a gap either way is worth knowing about.
        /// </summary>
        static string Messages(IReadOnlyCollection<GraphNode> components, string kind, string suffix)
        {
            var named = components.Count(c => c.Role == kind);
            var found = components.Count(c => c.Message == kind);
            var both = components.Count(c => c.Role == kind && c.Message == kind);
            return $"{named} {kind}s by name (`*{suffix}`), {found} by structure, {both} of them both";
        }

        static string Percent(int part, int total) => total == 0 ? "0%" : $"{100 * part / total}%";
    }
}
