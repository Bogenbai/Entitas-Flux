using System;
using System.Collections.Generic;
using System.Linq;

namespace Entitas.Graph.Output
{
    public sealed partial class GraphQueries
    {
        /// <summary>flow: system hops followed from the start (its takers are the first).</summary>
        public const int DefaultFlowDepth = 4, MaxFlowDepth = 8;

        /// <summary>How much of a flow is spelled out: less, step by step, until it fits the budget.</summary>
        enum FlowDetail
        {
            Full,

            /// <summary>Without the writes that are not followed ("also, not followed") and the counts of reactors a change misses.</summary>
            NoAlso,

            /// <summary>Also, the takers of a hand-off that hand nothing on share one line.</summary>
            FoldedEnds,

            /// <summary>Also without calls that hand nothing on and the readers outside the systems.</summary>
            Bare
        }

        /// <summary>
        /// A flow is folded once it passes this share of the budget, not only once it is over: one
        /// more branch on a flow at 1 990 of 2 000 tokens cut its last endings, and the cut falls
        /// on whatever comes last, which is often the main chain's end.
        /// </summary>
        const int FlowHeadroomPercent = 95;

        /// <summary>
        /// What happens after a component is created, or after a system runs, in one answer: the
        /// systems the data passes through — requests, events and [Watched] changes, each with its
        /// takers — where each one runs, in which frame it sees the data, and where it ends
        /// (<see cref="FlowWalk"/> has the rules). Assistants asked "what happens after the cart is
        /// committed" followed events one hop at a time and stopped before the second-order
        /// branches; one put the wallet change in the same frame, when it comes in the next.
        /// </summary>
        public string Flow(string name, int depth = DefaultFlowDepth)
        {
            var start = Resolve(name);
            if (start.Kind == NodeKind.Feature)
                return $"{start.Label} is a feature: flow follows data from a component or a system (systems-of {start.Label} lists what it runs).\n";

            var walk = FlowWalk.Run(_graph, start, Math.Max(1, Math.Min(MaxFlowDepth, depth)), _hubThreshold);
            var head = FlowHead(walk, NamesMarker(name, start));

            // Whole when it fits with room to spare; otherwise first without what is not followed —
            // side notes, the least of the answer — then with the endings folded, then bare, so
            // what is cut last is the main chain. Below that the budget cuts as usual.
            var entries = new FlowText(this, walk, FlowDetail.Full).Entries;
            foreach (var detail in new[] { FlowDetail.NoAlso, FlowDetail.FoldedEnds, FlowDetail.Bare })
            {
                if (!Capped || head.Sum(l => l.Length + Environment.NewLine.Length) + entries.Sum(e => e.Length) <=
                    _budget * CharsPerToken * FlowHeadroomPercent / 100)
                    break;
                entries = new FlowText(this, walk, detail).Entries;
            }

            return Render(head, () => FlowSummary(walk), entries, "lines");
        }

        List<string> FlowHead(FlowWalk walk, bool marker)
        {
            var start = walk.Start;
            var traits = start.Kind == NodeKind.Component ? ComponentTraits(start) : Traits(start);
            var roots = walk.Root == null
                ? "; no root feature runs it, so no frames"
                : $"; frames under {Label(walk.Root)}" +
                  (walk.OtherRoots.Count > 0 ? $" (also under {string.Join(", ", walk.OtherRoots.Select(Label))}, noted where they differ)" : string.Empty);
            var head = new List<string> { $"flow {start.Label}  [{traits}] — {Count(walk.Systems, "system")}, {Count(walk.DeepestHop, "hop")}{roots}" };
            if (marker)
                head.Add($"  {MarkerName(start)} is {start.Label}'s [Watched] marker: shown are the systems reacting to {start.Label}'s changes.");
            if (walk.StartLink != null)
                head.Add("  " + CreatedBy(walk));
            else if (Triggers(start) is { } triggers)
                head.Add("  " + triggers);
            head.Add("  each frame is counted from the system above; [+N]: frames after the step marked [+0]");
            return head;
        }

        /// <summary>The line of context above a component's flow: who creates it, and where that runs.</summary>
        string CreatedBy(FlowWalk walk)
        {
            var creators = walk.Creators.Select(e => e.Source).Distinct().Select(id => _graph.Node(id)!)
                .OrderBy(n => n.Kind == NodeKind.System ? 0 : 1)
                .ThenBy(n => walk.Root == null ? int.MaxValue : FlowWalk.Order(n, walk.Root) ?? int.MaxValue)
                .ThenBy(n => n.Label, StringComparer.Ordinal)
                .ToList();
            if (creators.Count == 0)
                return "nothing in the code creates it (data, a prefab, or nobody)";

            var counted = walk.Producers.Count > 0 ? " [+0]" : string.Empty;
            var verb = walk.StartLink!.Kind.HasFlag(HandOff.Watched) ? "changed" : "created";
            string Name(GraphNode n) => n.Kind == NodeKind.System ? $"{n.Label} {FlowPlace(n, walk.Root)}" : $"{n.Label} [{n.Kind}]";
            if (creators.Count == 1)
                return $"{verb} by {Name(creators[0])}{counted}{If(walk.Creators)}  {ShortLocation(walk.Creators[0])}";

            const int shown = 3;
            var some = walk.Producers.Count > 0 && walk.Producers.Count < creators.Count
                ? $"; frames count from the {walk.Producers.Count} running under {Label(walk.Root!)}"
                : string.Empty;
            return $"{verb} by {creators.Count}{counted}: {string.Join(", ", creators.Take(shown).Select(Name))}" +
                   (creators.Count > shown ? $", +{creators.Count - shown} more (who-creates {walk.Start.Label})" : string.Empty) + some;
        }

        /// <summary>For a system start: the requests, events and [Watched] changes it takes, the line of context above its flow.</summary>
        string? Triggers(GraphNode system)
        {
            var taken = _graph.Outgoing(system.Id)
                .Where(e => e.Relation is Relation.Reads or Relation.Reacts && e.Confidence == Confidence.Extracted)
                .Select(e => (Node: _graph.Node(e.Target)!, e.Relation))
                .Where(t => t.Relation == Relation.Reacts || t.Node.MessageRole != null)
                .Select(t => t.Relation == Relation.Reacts ? $"{MarkerName(t.Node)} ([Watched])" : $"{t.Node.Label} ({t.Node.MessageRole})")
                .Distinct()
                .OrderBy(s => s, StringComparer.Ordinal)
                .ToList();
            return taken.Count == 0 ? null : "takes " + string.Join(", ", taken);
        }

        /// <summary>
        /// Where the answer was cut, its shape: how many systems and hand-offs, how deep, and per
        /// branch the systems and hand-offs it reaches — so a branch the budget left out is still
        /// named, with what to ask next.
        /// </summary>
        IEnumerable<string> FlowSummary(FlowWalk walk)
        {
            var beyond = walk.BeyondDepth > 0 ? $"; --depth {walk.Depth} left the takers of {walk.BeyondDepth} hand-offs out" : string.Empty;
            yield return $"  flow: {Count(walk.Systems, "system")}, {Count(walk.HandOffs, "hand-off")}, {Count(walk.DeepestHop, "hop")}{beyond}";

            var branches = walk.StartStep != null
                ? walk.StartStep.AllHandOffs.ToList()
                : walk.StartLink!.Takers.Where(t => t.Step.Expanded).SelectMany(t => t.Step.AllHandOffs).ToList();
            if (walk.StartLink != null)
                yield return "  taken by: " + Names(walk.StartLink.Takers.Select(t => t.Step.Node.Label), 6);
            foreach (var branch in branches.Where(b => b.Expanded))
            {
                var systems = new List<string>();
                var handOffs = new List<string>();
                Gather(branch, systems, handOffs);
                var taken = systems.Count > 0 ? Names(systems.Distinct(), 3)
                    : branch.Beyond > 0 ? $"{Count(branch.Beyond, "system")} past --depth {walk.Depth}"
                    : "no system takes it";
                var condition = FlowWalk.ConditionOf(branch.Writes) is { } c ? $" (if {c})" : string.Empty;
                yield return $"  {branch.Component.Label}{condition} → {taken}" + (handOffs.Count > 0 ? $"; then {Names(handOffs, 5)}" : string.Empty);
            }

            // Every taker is named, also one expanded under another branch; only expanded ones lead on.
            static void Gather(FlowLink link, List<string> systems, List<string> handOffs)
            {
                foreach (var take in link.Takers)
                {
                    systems.Add(take.Step.Node.Label);
                    if (!take.Step.Expanded)
                        continue;
                    foreach (var next in take.Step.AllHandOffs.Where(h => h.Expanded))
                    {
                        handOffs.Add(next.Component.Label);
                        Gather(next, systems, handOffs);
                    }
                }
            }

            static string Names(IEnumerable<string> names, int shown)
            {
                var list = names.ToList();
                return string.Join(", ", list.Take(shown)) + (list.Count > shown ? $", +{list.Count - shown} more" : string.Empty);
            }
        }

        static string Count(int n, string noun) => $"{n} {noun}{(n == 1 ? string.Empty : "s")}";

        /// <summary>"  (if amount &lt; 0)" when every write on the line happens under a condition.</summary>
        static string If(IEnumerable<GraphEdge> edges) => If(FlowWalk.ConditionOf(edges));

        static string If(string? condition) => condition == null ? string.Empty : $"  (if {condition})";

        /// <summary>A system's place as flow lists it: its order under the flow's root, or where it runs instead.</summary>
        string FlowPlace(GraphNode node, string? root)
        {
            var placement = root == null ? null : node.Placements.FirstOrDefault(p => p.Root == root);
            if (placement != null)
                return $"#{placement.Order}{InFeature(placement)}{Phase(node)}";
            if (node.Placements.Count > 0)
            {
                var first = node.Placements[0];
                return $"{Label(first.Root)} #{first.Order}{InFeature(first)}{Phase(node)}";
            }
            return node.Kind == NodeKind.System ? "not in any feature" : $"[{node.Kind}]";
        }

        /// <summary>The feature registering it, unless that is the root itself.</summary>
        string InFeature(Placement placement) =>
            placement.Chain.Count > 0 && placement.Chain[placement.Chain.Count - 1] != placement.Root
                ? $" ({Label(placement.Chain[placement.Chain.Count - 1])})"
                : string.Empty;

        /// <summary>A cleanup system runs after every execute system of its root, whatever its number says.</summary>
        static string Phase(GraphNode node) =>
            node.Interfaces.Count > 0 && !node.Interfaces.Contains("Execute") ? ", " + string.Join("/", node.Interfaces) : string.Empty;

        /// <summary>
        /// The flow as entries of one line each, in reading order: a tree, indented by hop, one fact
        /// per line with its location at the end. A node shown before is referred to, not repeated;
        /// a gate or a generic destroyer is spelled out the first time only.
        /// </summary>
        sealed class FlowText
        {
            readonly GraphQueries _q;
            readonly FlowWalk _walk;
            readonly FlowDetail _detail;
            readonly HashSet<string> _shown = new HashSet<string>(StringComparer.Ordinal);
            readonly HashSet<string> _named = new HashSet<string>(StringComparer.Ordinal);

            public readonly List<string> Entries = new List<string>();

            public FlowText(GraphQueries queries, FlowWalk walk, FlowDetail detail)
            {
                _q = queries;
                _walk = walk;
                _detail = detail;

                if (walk.StartStep != null)
                {
                    var step = walk.StartStep;
                    _shown.Add(step.Node.Id);
                    Line($"{step.Node.Label}  {_q.FlowPlace(step.Node, walk.Root)}{Since(step)}{DeclaredIn(step.Node)}");
                    Writes(step, "  ");
                }
                else
                {
                    _shown.Add(walk.StartLink!.Component.Id);
                    Takers(walk.StartLink, "  ");
                    Ends(walk.StartLink, "  ");
                }
            }

            void Line(string text) => Entries.Add(text + "\n");

            string Where(string id) => _shown.Contains(id) ? "  (see above)" : "  (see below)";

            void Writes(FlowStep step, string indent)
            {
                foreach (var link in step.HandOffs)
                    Link(link, indent, string.Empty, null);

                if (_detail == FlowDetail.Full && step.NotFollowed.Count > 0)
                    Line($"{indent}also, not followed: {NotFollowed(step.NotFollowed)}");

                // After the system's own writes, what it writes by calling a factory or a helper:
                // the system is the subject of the line, as of every line at this indent.
                foreach (var call in step.Calls)
                {
                    if (_detail == FlowDetail.Bare && call.HandOffs.Count == 0)
                        continue;
                    if (call.Writes.Count == 1 && call.HandOffs.Count == 1)
                    {
                        Link(call.HandOffs[0], indent, $"  by calling {call.Method}", null);
                        continue;
                    }

                    // Located in the called method itself, not in a helper it calls in turn.
                    var first = call.Edges.OrderBy(e => call.Methods.Contains(e.Through ?? string.Empty) ? 0 : 1)
                        .ThenBy(e => e.SourceFile, StringComparer.Ordinal).ThenBy(e => e.Line).First();
                    var condition = FlowWalk.ConditionOf(call.Edges);
                    Line($"{indent}by calling {call.Method}: {CallWrites(call)}{If(condition)}  {ShortLocation(first)}");
                    foreach (var link in call.HandOffs)
                        Link(link, indent + "  ", string.Empty, condition);
                }
            }

            /// <param name="call">"  by calling X" when the call's line is this one.</param>
            /// <param name="above">The condition of the call line above: the same one is not said twice.</param>
            void Link(FlowLink link, string indent, string call, string? above)
            {
                var condition = FlowWalk.ConditionOf(link.Writes);
                Line($"{indent}{WriteVerb(link.Relations)} {link.Component.Label}  [{Tag(link)}]{call}{(condition == above ? string.Empty : If(condition))}  {ShortLocation(link.Writes[0])}" +
                     (link.Expanded ? string.Empty : Where(link.Component.Id)));
                if (!link.Expanded)
                    return;
                _shown.Add(link.Component.Id);
                Takers(link, indent + "  ");
                if (link.Beyond > 0)
                    Line($"{indent}  … taken by {link.Beyond} more system{(link.Beyond == 1 ? string.Empty : "s")}, past --depth {_walk.Depth}");
                Ends(link, indent + "  ");
            }

            /// <summary>
            /// "creates 9 (ChangeHueRequest, Scale, TutorialTargetGhost, …); removes 2 (Moving, Velocity)":
            /// per verb the count, then the names — followed ones first, then other hand-offs, then
            /// state. Counts only when the answer is cut to the bone.
            /// </summary>
            string CallWrites(FlowCall call)
            {
                const int shown = 3;
                var followed = new HashSet<string>(call.HandOffs.Select(h => h.Component.Id), StringComparer.Ordinal);
                return string.Join("; ", call.Writes
                    .GroupBy(w => string.Join("+", w.Edges.Select(e => e.Relation).Distinct().OrderBy(r => r == Relation.Creates ? 0 : 1)))
                    .OrderBy(g => g.Key, StringComparer.Ordinal)
                    .Select(g =>
                    {
                        var count = g.Count();
                        if (_detail == FlowDetail.Bare)
                            return $"{g.Key} {count}";
                        var names = g.OrderBy(w => followed.Contains(w.Component.Id) ? 0 : 1)
                            .ThenBy(w => w.Component.MessageRole != null || w.Component.Attributes.Contains("Watched") ? 0 : 1)
                            .ThenBy(w => w.Hub ? 1 : 0)
                            .ThenBy(w => w.Component.Label, StringComparer.Ordinal)
                            .Select(w => w.Component.Label + (w.Hub ? " (hub)" : string.Empty))
                            .ToList();
                        return count == 1
                            ? $"{g.Key} {names[0]}"
                            : $"{g.Key} {count} ({string.Join(", ", names.Take(shown))}{(count > shown ? ", …" : string.Empty)})";
                    }));
            }

            void Takers(FlowLink link, string indent)
            {
                var ending = new List<FlowTake>();
                foreach (var take in link.Takers)
                {
                    if (_detail >= FlowDetail.FoldedEnds && (take.Frame.Never || take.Step.Expanded && !take.Step.HandsOn))
                    {
                        ending.Add(take);
                        continue;
                    }

                    var step = take.Step;
                    Line($"{indent}{TakeVerb(take)} {step.Node.Label}{MayMiss(take)}{Inferred(take.Edges)}  {_q.FlowPlace(step.Node, _walk.Root)}  {Frame(take)}{Since(step)}{Until(step)}  " +
                         ShortLocation(take.Edges[0]) + (step.Expanded || take.Frame.Never ? string.Empty : Where(step.Node.Id)));
                    if (!step.Expanded)
                        continue;
                    _shown.Add(step.Node.Id);
                    Writes(step, indent + "  ");
                }

                if (_detail == FlowDetail.Full && link.Skipped > 0)
                    Line(link.Relations.Contains(Relation.Creates)
                        ? $"{indent}+{link.Skipped} reacting to {MarkerName(link.Component)} take only its removal (their groups leave {link.Component.Label} out)"
                        : $"{indent}+{link.Skipped} reacting to {MarkerName(link.Component)} take only its add (their groups require {link.Component.Label})");

                // Named by what they need, not by name: the reader looking for one learns why it
                // is missing, and the systems themselves are no part of this flow.
                if (_detail == FlowDetail.Full && link.Unseen.Count > 0)
                    Line($"{indent}+{link.Unseen.Count} reacting to {MarkerName(link.Component)} need {Needs(link.Unseen)}, which this entity never gets");

                if (ending.Count == 0)
                    return;

                // Takers that end there (a sound, a statistic, an achievement) on one line: what the
                // chain leads to is in the lines above.
                foreach (var take in ending.Where(t => t.Step.Expanded))
                    _shown.Add(take.Step.Node.Id);
                var frames = ending.Select(t => Frame(t, spell: false) + Since(t.Step) + Until(t.Step)).Distinct().ToList();
                var verb = ending.Select(TakeVerb).Distinct().Count() == 1 ? TakeVerb(ending[0]) : "taken by";
                Line(frames.Count == 1
                    ? $"{indent}{verb}, ending there: {string.Join(", ", ending.Select(t => $"{t.Step.Node.Label}{MayMiss(t)} {_q.FlowPlace(t.Step.Node, _walk.Root)}"))} — {Frame(ending[0])}{Since(ending[0].Step)}{Until(ending[0].Step)}"
                    : $"{indent}{verb}, ending there: {string.Join("; ", ending.Select(t => $"{t.Step.Node.Label}{MayMiss(t)} {_q.FlowPlace(t.Step.Node, _walk.Root)} {Frame(t)}{Since(t.Step)}{Until(t.Step)}"))}");
            }

            /// <summary>
            /// " (the same call may take off CatAnimator, which it needs)": a reactor the change
            /// may never reach, since the call making it may take that off the entity too (graph.json
            /// `may_miss`). Not said of one that never sees it anyway, under any root.
            /// </summary>
            static string MayMiss(FlowTake take) =>
                take.MayMiss == null || take.Frame.Never && take.Elsewhere.All(e => e.Frame.Never)
                    ? string.Empty
                    : $" (the same call may take off {take.MayMiss}, which it needs)";

            void Ends(FlowLink link, string indent)
            {
                foreach (var end in link.Ends)
                {
                    if (_detail == FlowDetail.Bare && end.Kind == FlowEnd.Outside)
                        continue;
                    Line($"{indent}ends: {End(link, end)}");
                }
            }

            string End(FlowLink link, FlowEnd end)
            {
                var place = _q.FlowPlace(end.By, _walk.Root);
                switch (end.Kind)
                {
                    case FlowEnd.Cleared:
                        return $"{MarkerName(link.Component)} cleared by {end.By.Label}  {place}, generated";
                    case FlowEnd.GeneratedReader:
                        return $"read by {end.By.Label}  {place}, generated";
                    case FlowEnd.Outside:
                        return $"read outside the systems by {string.Join(", ", end.Readers.Take(4).Select(r => r.Label))}" +
                               (end.Readers.Count > 4 ? $", +{end.Readers.Count - 4} more" : string.Empty);
                    case FlowEnd.Nothing:
                        return "nothing removes or destroys it";
                }

                var edge = end.Edges[0];
                var inferred = edge.Confidence == Confidence.Inferred;
                var how = end.By.Generated ? ", generated"
                    : !inferred || edge.Through == null ? string.Empty
                    : _named.Add("end\u0001" + end.By.Id) ? $"  ({edge.Through})"
                    : "  (as above)";
                return $"{end.Kind} by {end.By.Label}{(inferred ? "*" : string.Empty)}  {place}{how}{If(end.Edges)}  {ShortLocation(edge)}";
            }

            /// <summary>
            /// "same frame", "next frame (waits for Ready from MarkEventsReadySystem #2)", "frame
            /// unknown (why)", "never sees it (why)" — and what differs under the start's other roots.
            /// A reaction never seen names what else the system reacts to, once.
            /// </summary>
            string Frame(FlowTake take, bool spell = true)
            {
                var others = take.OtherTriggers.Count == 0 ? null : OtherTriggers(take.OtherTriggers);
                var text = Words(take.Frame, spell, take.Frame.Never ? others : null);
                if (take.Frame.Never)
                    others = null;
                foreach (var (root, frame) in take.Elsewhere)
                {
                    text += $" ({_q.Label(root)}: {Words(frame, false, frame.Never ? others : null)})";
                    if (frame.Never)
                        others = null;
                }
                return text;
            }

            /// <summary>"other trigger: ProductStandAnimatorChanged", three named at most.</summary>
            static string OtherTriggers(IReadOnlyList<GraphNode> components)
            {
                const int shown = 3;
                return (components.Count == 1 ? "other trigger: " : "other triggers: ") +
                       string.Join(", ", components.Take(shown).Select(MarkerName)) +
                       (components.Count > shown ? $", +{components.Count - shown} more" : string.Empty);
            }

            /// <summary>"Cat", "Cat or Plant": what the reactors a write misses need.</summary>
            static string Needs(IEnumerable<FlowUnseen> unseen) =>
                string.Join(" or ", unseen.Select(u => u.Needs).Distinct().OrderBy(n => n, StringComparer.Ordinal));

            string Words(FlowFrame frame, bool spell, string? others = null)
            {
                if (frame.Never)
                    return $"never sees it ({frame.Why}{(others == null ? string.Empty : "; " + others)})";
                if (frame.Frames == null)
                    return $"frame unknown ({frame.Why})";
                var words = frame.Frames == 0 ? "same frame" : frame.Frames == 1 ? "next frame" : $"{frame.Frames} frames later";
                if (frame.Gate == null)
                    return words;
                var gate = frame.Gate;
                return spell && _named.Add("gate\u0001" + gate.Component.Id + "\u0001" + gate.Setter.Id)
                    ? $"{words} (waits for {gate.Component.Label} from {gate.Setter.Label} #{frame.GateOrder}{(gate.More > 0 ? $" or {gate.More} more" : string.Empty)})"
                    : $"{words} (waits for {gate.Component.Label})";
            }

            static string Since(FlowStep step) => step.Since is { } since ? $" [+{since}]" : string.Empty;

            /// <summary>", destroys it only once target.hasHueAnimator (HueAnimator can arrive after an await)": the message waits.</summary>
            static string Until(FlowStep step) => step.Until is { } until
                ? $", {until.Verb} it only once {until.Condition}" + (until.Late != null ? $" ({until.Late})" : string.Empty)
                : string.Empty;

            static string WriteVerb(IReadOnlyList<string> relations) => string.Join("+", relations);

            static string TakeVerb(FlowTake take) =>
                string.Join("+", take.Edges.Select(e => e.Relation switch
                {
                    Relation.Reacts => "reacted to",
                    Relation.Reads => "read",
                    Relation.Destroys => "destroyed",
                    Relation.Removes => "removed",
                    _ => e.Relation
                }).Distinct()) + " by";

            static string Tag(FlowLink link)
            {
                var tags = new List<string>();
                if (link.Kind.HasFlag(HandOff.Request)) tags.Add("request");
                if (link.Kind.HasFlag(HandOff.Event)) tags.Add("event");
                if (link.Kind.HasFlag(HandOff.Transient)) tags.Add("transient");
                if (link.Kind.HasFlag(HandOff.Watched)) tags.Add($"Watched → {MarkerName(link.Component)}");
                return string.Join(", ", tags);
            }

            /// <summary>For a taker reading only through code it calls: `via AllOf* (FurnitureQuery.Count)`, as elsewhere.</summary>
            static string Inferred(IReadOnlyList<GraphEdge> edges)
            {
                if (edges.Count == 0 || edges.Any(e => e.Confidence == Confidence.Extracted))
                    return string.Empty;
                var vias = string.Join(", ", edges.Select(e => e.Via).Distinct());
                var through = edges.Where(e => e.Through != null).Select(e => e.Through!).Distinct().ToList();
                return $"  via {vias}*" + (through.Count == 0 ? string.Empty : $" ({string.Join("; ", through.Take(2))}{(through.Count > 2 ? $"; +{through.Count - 2} more" : string.Empty)})");
            }

            /// <summary>"creates Placed, PaidPrice, Event* (hub); removes Highlighted": written in the system first, five at most per verb.</summary>
            static string NotFollowed(IEnumerable<FlowWrite> writes)
            {
                const int shown = 5;
                return string.Join("; ", writes
                    .GroupBy(w => string.Join("+", w.Edges.Select(e => e.Relation).Distinct().OrderBy(r => r == Relation.Creates ? 0 : 1)))
                    .OrderBy(g => g.Key, StringComparer.Ordinal)
                    .Select(g =>
                    {
                        var names = g.OrderBy(w => w.Edges.Any(e => e.Confidence == Confidence.Extracted) ? 0 : 1)
                            .ThenBy(w => w.Component.Label, StringComparer.Ordinal)
                            .Select(w => w.Component.Label + (w.Edges.Any(e => e.Confidence == Confidence.Extracted) ? string.Empty : "*") +
                                         (w.Hub ? " (hub)" : string.Empty) + Missed(w))
                            .ToList();
                        return $"{g.Key} {string.Join(", ", names.Take(shown))}{(names.Count > shown ? $", +{names.Count - shown} more" : string.Empty)}";
                    }));
            }

            /// <summary>Why the reactors of a [Watched] write listed, not followed, do not take it: " (its 4 reactors take the other change)".</summary>
            static string Missed(FlowWrite write)
            {
                var why = new List<string>();
                if (write.Skipped > 0)
                    why.Add($"{Count(write.Skipped, "reactor")} take{(write.Skipped == 1 ? "s" : string.Empty)} the other change");
                if (write.Unseen.Count > 0)
                    why.Add($"{Count(write.Unseen.Count, "reactor")} need{(write.Unseen.Count == 1 ? "s" : string.Empty)} {Needs(write.Unseen)}, which it never gets");
                return why.Count == 0 ? string.Empty : $" (its {string.Join("; ", why)})";
            }
        }
    }
}
