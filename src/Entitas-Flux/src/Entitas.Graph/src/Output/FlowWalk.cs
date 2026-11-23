using System;
using System.Collections.Generic;
using System.Linq;

namespace Entitas.Graph.Output
{
    /// <summary>What makes a component written by a system the start of somebody else's work.</summary>
    [Flags]
    internal enum HandOff
    {
        None = 0,
        Request = 1,
        Event = 2,

        /// <summary>No entity keeps it ([Cleanup], or <see cref="GraphNode.TransientRemovedBy"/>): it is there to be acted on.</summary>
        Transient = 4,

        /// <summary>A [Watched] component systems of the project react to: changing it sets XChanged.</summary>
        Watched = 8,

        /// <summary>Only for the start: a plain component, whose readers are what the question is about.</summary>
        State = 16
    }

    /// <summary>
    /// What `flow` answers with: the systems data passes through after a component is created or
    /// a system runs, and in which frame each of them sees it. Pure data; <see cref="GraphQueries"/>
    /// turns it into text.
    ///
    /// The walk follows hand-offs only — requests, events, the change of a [Watched] component
    /// (to the systems reading its XChanged marker), components no entity keeps — because those
    /// are what start another system's work. A plain component a system writes is state: whoever
    /// reads it does so whenever it runs, not because of this write, and following it reaches
    /// half the project in two steps (Placed has 44 readers on a 2000-file project). Such writes
    /// are listed, not followed; so are writes of hubs (Event, Id) and anything a generated system
    /// does. What a system writes only through a method it calls is one line per call (<see
    /// cref="Calls"/>). Every node is expanded once, at the fewest hops (breadth-first), and
    /// referenced after. A write made under a condition carries it (<see cref="ConditionOf"/>).
    ///
    /// Frames come from placements alone. Under one root feature the execute systems run in their
    /// order and the cleanup systems after all of them; a system running after the producer sees
    /// the data in the same frame, one running before it in the next. What delays a reader beyond
    /// that is a gate the graph knows for sure: a component the reader's group requires and that
    /// another system adds to the entity later — Ready, which MarkEventsReadySystem adds to every
    /// Event entity, as the `through` of an inferred destroy records ("created with Event + X;
    /// Ready by MarkEventsReadySystem"). The build records the other such gates on the write
    /// (graph.json `waits`, Analysis.Waits): a component the reader's groups require that the
    /// entity the producer has just made is made without — the reader runs after the system
    /// adding it, or the frame is unknown when it is added after an await or outside the systems
    /// (the View a view factory adds once the asset is loaded). What can stop a reader from seeing
    /// it at all is a system taking the component off before it runs (a handler destroying its
    /// request, the generated cleanup clearing XChanged). Anything else — a system under another
    /// root, a creator outside the systems, a gate only some creations get — is "frame unknown",
    /// never a guess. Where the entity or the reader's way to it is not known for certain, no gate
    /// is recorded: a frame says when the taker can see the data first.
    /// </summary>
    internal sealed class FlowWalk
    {
        readonly EcsGraph _graph;
        readonly int _hubThreshold;
        readonly Dictionary<string, List<GraphEdge>> _out = new Dictionary<string, List<GraphEdge>>(StringComparer.Ordinal);
        readonly Dictionary<string, List<GraphEdge>> _in = new Dictionary<string, List<GraphEdge>>(StringComparer.Ordinal);
        readonly Dictionary<string, int> _written = new Dictionary<string, int>(StringComparer.Ordinal);
        readonly Dictionary<string, List<FlowGate>> _gates = new Dictionary<string, List<FlowGate>>(StringComparer.Ordinal);
        readonly Dictionary<string, List<FlowWait>> _waits = new Dictionary<string, List<FlowWait>>(StringComparer.Ordinal);
        readonly HashSet<string> _expandedSystems = new HashSet<string>(StringComparer.Ordinal);
        readonly HashSet<string> _expandedComponents = new HashSet<string>(StringComparer.Ordinal);
        readonly Queue<FlowStep> _queue = new Queue<FlowStep>();

        public GraphNode Start { get; }
        public int Depth { get; }

        /// <summary>The root feature frames are counted under; null when no root runs the start.</summary>
        public string? Root { get; private set; }

        /// <summary>Other roots the start runs under (two at most): a frame that differs there is noted.</summary>
        public IReadOnlyList<string> OtherRoots { get; private set; } = Array.Empty<string>();

        /// <summary>The start, when it is a component: its takers are the first hop.</summary>
        public FlowLink? StartLink { get; private set; }

        /// <summary>The start, when it is a system (or another type): its writes are where the walk begins.</summary>
        public FlowStep? StartStep { get; private set; }

        /// <summary>For a component start: what creates it, the line of context above the walk.</summary>
        public IReadOnlyList<GraphEdge> Creators { get; private set; } = Array.Empty<GraphEdge>();

        /// <summary>
        /// For a component start: the systems creating it under <see cref="Root"/>, which the first
        /// hop's frames are counted from. A taker gets a frame when they all agree on it; a window
        /// or a debug console creating it too is left out, since it does so whenever it is used.
        /// </summary>
        public IReadOnlyList<GraphNode> Producers { get; private set; } = Array.Empty<GraphNode>();

        public int Systems => _expandedSystems.Count;
        public int HandOffs { get; private set; }
        public int DeepestHop { get; private set; }

        /// <summary>Hand-offs at the last hop whose takers --depth left out.</summary>
        public int BeyondDepth { get; private set; }

        public static FlowWalk Run(EcsGraph graph, GraphNode start, int depth, int hubThreshold)
        {
            var walk = new FlowWalk(graph, start, depth, hubThreshold);
            walk.Walk();
            walk.Arrange();
            return walk;
        }

        FlowWalk(EcsGraph graph, GraphNode start, int depth, int hubThreshold)
        {
            _graph = graph;
            Start = start;
            Depth = depth;
            _hubThreshold = hubThreshold;
            foreach (var edge in graph.Edges)
            {
                if (edge.Relation == Relation.Contains)
                    continue;
                Add(_out, edge.Source, edge);
                Add(_in, edge.Target, edge);
            }
        }

        static void Add(Dictionary<string, List<GraphEdge>> index, string id, GraphEdge edge)
        {
            if (!index.TryGetValue(id, out var list))
                index[id] = list = new List<GraphEdge>();
            list.Add(edge);
        }

        IReadOnlyList<GraphEdge> Out(string id) => _out.TryGetValue(id, out var list) ? list : (IReadOnlyList<GraphEdge>)Array.Empty<GraphEdge>();
        IReadOnlyList<GraphEdge> In(string id) => _in.TryGetValue(id, out var list) ? list : (IReadOnlyList<GraphEdge>)Array.Empty<GraphEdge>();
        GraphNode Node(string id) => _graph.Node(id)!;

        // -- the walk ----------------------------------------------------------------------------

        void Walk()
        {
            if (Start.Kind == NodeKind.Component)
            {
                // Every write of it: a reactor is left out only when none of them reaches it.
                var writes = In(Start.Id).Where(e => e.Relation is Relation.Creates or Relation.Removes).ToList();
                var kind = Kind(Start, new[] { Relation.Creates, Relation.Removes }, writes);
                StartLink = new FlowLink(Start, kind == HandOff.None ? HandOff.State : kind, Array.Empty<GraphEdge>(), Array.Empty<string>()) { Expanded = true };
                _expandedComponents.Add(Start.Id);

                // For a [Watched] component the change is what is followed, and a removal is one too.
                var changes = StartLink.Kind.HasFlag(HandOff.Watched);
                Creators = In(Start.Id).Where(e => e.Relation == Relation.Creates || changes && e.Relation == Relation.Removes)
                    .OrderBy(e => e.Confidence == Confidence.Extracted ? 0 : 1).ThenBy(e => e.Source, StringComparer.Ordinal).ThenBy(e => e.Line)
                    .ToList();

                var (takers, skipped, unseen) = Takers(Start, StartLink.Kind, StartLink.Relations, null, writes);
                StartLink.Skipped = skipped;
                StartLink.Unseen.AddRange(unseen);
                var creators = Creators.Select(e => Node(e.Source)).Distinct().ToList();
                ChooseRoots(takers.Select(t => t.Node), creators);
                Producers = Root == null ? Array.Empty<GraphNode>() : creators.Where(c => c.Kind == NodeKind.System && Slot(c, Root) != null).ToList();
                Take(StartLink, Producers, 1, Producers.Count > 0 ? 0 : (int?)null, takers);
            }
            else
            {
                StartStep = new FlowStep(Start, 0) { Expanded = true };
                _expandedSystems.Add(Start.Id);
                ChooseRoots(Start.Placements.Count > 0
                    ? new[] { Start }
                    : Writes(Start).SelectMany(w => w.Kind == HandOff.None ? Enumerable.Empty<GraphNode>() : Takers(w.Component, w.Kind, w.Relations, Start, w.Edges).Takers.Select(t => t.Node)),
                    Array.Empty<GraphNode>());
                StartStep.Since = Root != null && Slot(Start, Root) != null ? 0 : (int?)null;
                _queue.Enqueue(StartStep);
            }

            while (_queue.Count > 0)
                Expand(_queue.Dequeue());
        }

        /// <summary>
        /// A system start runs under its own roots. For a component, the roots its takers run under,
        /// those where a creator runs too first — CartCommittedEvent is created under
        /// BuildingModeFeature, and one of its readers runs under GameplayCoreFeature as well — then
        /// the one most takers run under.
        /// </summary>
        void ChooseRoots(IEnumerable<GraphNode> takers, IReadOnlyList<GraphNode> creators)
        {
            var nodes = takers.ToList();
            var candidates = nodes.SelectMany(n => n.Placements.Select(p => p.Root)).Distinct().ToList();
            if (candidates.Count == 0)
                candidates = creators.SelectMany(n => n.Placements.Select(p => p.Root)).Distinct().ToList();
            var roots = Start.Kind != NodeKind.Component && Start.Placements.Count > 0
                ? candidates
                : candidates
                    .Select((root, index) => (Root: root, Index: index))
                    .OrderBy(r => creators.Any(c => c.Kind == NodeKind.System && Order(c, r.Root) != null) ? 0 : 1)
                    .ThenByDescending(r => nodes.Count(n => Order(n, r.Root) != null))
                    .ThenBy(r => r.Index)
                    .Select(r => r.Root)
                    .ToList();
            Root = roots.FirstOrDefault();
            OtherRoots = roots.Skip(1).Take(2).ToList();
        }

        /// <summary>What a system writes, a component at a time, hand-offs in the order they are followed.</summary>
        IEnumerable<(GraphNode Component, HandOff Kind, IReadOnlyList<GraphEdge> Edges, IReadOnlyList<string> Relations)> Writes(GraphNode system) =>
            Out(system.Id)
                .Where(e => e.Relation is Relation.Creates or Relation.Removes)
                .GroupBy(e => e.Target, StringComparer.Ordinal)
                .Select(g =>
                {
                    var component = Node(g.Key);
                    var relations = g.Select(e => e.Relation).Distinct().OrderBy(r => r == Relation.Creates ? 0 : 1).ToList();
                    var edges = g.OrderBy(e => e.Confidence == Confidence.Extracted ? 0 : 1).ThenBy(e => e.SourceFile, StringComparer.Ordinal).ThenBy(e => e.Line).ToList();
                    return (Component: component, Kind: IsHub(component) ? HandOff.None : Kind(component, relations, edges), Edges: (IReadOnlyList<GraphEdge>)edges, Relations: (IReadOnlyList<string>)relations);
                })
                .Where(w => w.Component.Kind == NodeKind.Component)
                .OrderBy(w => w.Edges[0].Confidence == Confidence.Extracted ? 0 : 1)
                .ThenBy(w => KindRank(w.Kind))
                .ThenBy(w => w.Edges[0].Line)
                .ThenBy(w => w.Component.Label, StringComparer.Ordinal);

        void Expand(FlowStep step)
        {
            DeepestHop = Math.Max(DeepestHop, step.Hop);
            var called = new List<(GraphNode Component, HandOff Kind, IReadOnlyList<GraphEdge> Edges, IReadOnlyList<string> Relations)>();
            foreach (var write in Writes(step.Node))
            {
                var (component, kind, edges, relations) = write;
                if (edges.All(e => e.Confidence == Confidence.Inferred))
                {
                    called.Add(write);
                    continue;
                }

                if (kind == HandOff.None)
                {
                    // "read+removed by X" says it already; "also, not followed: removes it" under X
                    // again did not. A removal handed on (a [Watched] change) is still followed.
                    if (component.Id == step.Takes && relations.All(r => r == Relation.Removes))
                        continue;

                    step.NotFollowed.Add(NotFollowed(component, edges, relations));
                    continue;
                }

                Follow(step, new FlowLink(component, kind, edges, relations), step.HandOffs);
            }

            Calls(step, called);
        }

        void Follow(FlowStep step, FlowLink link, List<FlowLink> into, (List<FlowTaker> Takers, int Skipped, List<FlowUnseen> Unseen)? known = null)
        {
            into.Add(link);
            if (!_expandedComponents.Add(link.Component.Id))
                return;

            link.Expanded = true;
            HandOffs++;
            var (takers, skipped, unseen) = known ?? Takers(link.Component, link.Kind, link.Relations, step.Node, link.Writes);
            link.Skipped = skipped;
            link.Unseen.AddRange(unseen);
            if (step.Hop >= Depth)
            {
                link.Beyond = takers.Count;
                if (takers.Count > 0)
                    BeyondDepth++;
                link.Ends.AddRange(Ends(link, step.Node, takers));
                return;
            }

            Take(link, new[] { step.Node }, step.Hop + 1, step.Since, takers);
        }

        /// <summary>
        /// What the system writes only through methods it calls — a factory building an entity, a
        /// helper changing one — goes on one line per call, after the system's own writes. A
        /// factory sets dozens of components, and each spelled out as a hand-off of its own took
        /// half of a flow on a 2000-file project, among the system's own events, where an assistant
        /// read the factory as the creator of the event above them. Methods writing a component in
        /// common share the line (TaskProgressRequests.Request and RequestSale both create the
        /// request), so no component is on two lines with half its conditions on each.
        ///
        /// Requests and events written that way are still followed, under the call's line: they
        /// are messages, sent on purpose. A [Watched] or transient component is followed only
        /// when the call changes it — a replace, a removal, a flag; an AddX there is the initial
        /// value of what a factory builds (MaxCapacity, Scale on new furniture). A [Watched] change
        /// is then followed whenever a system reacts to it, also one that never sees it: that the
        /// generated cleanup clears the marker before CatsAnimationSystem runs is likely a bug in
        /// the project, and folded into the call's line it went unseen. A transient component only
        /// when some system is known to see it (a frame, not "never" or "unknown"). A helper
        /// changing a live entity is followed (UsablesExtensions.UnOccupy, the role a hired cat
        /// gets from CatFactory.ConvertToWorker). Everything else is named on the line.
        /// A call that writes one component and hands nothing on is no line of its own: the write
        /// joins those not followed, marked inferred (`Event*` from CreateEntity.Event).
        /// </summary>
        void Calls(FlowStep step, List<(GraphNode Component, HandOff Kind, IReadOnlyList<GraphEdge> Edges, IReadOnlyList<string> Relations)> writes)
        {
            var leader = new Dictionary<string, string>(StringComparer.Ordinal);
            string Find(string method)
            {
                while (leader.TryGetValue(method, out var up) && up != method)
                    method = up;
                return method;
            }

            foreach (var write in writes)
            {
                var methods = write.Edges.SelectMany(CalledMethods).Distinct(StringComparer.Ordinal).ToList();
                foreach (var method in methods)
                    if (!leader.ContainsKey(method))
                        leader[method] = method;
                foreach (var method in methods.Skip(1))
                {
                    // The smaller name leads, so the groups do not depend on the order writes come in.
                    var (a, b) = (Find(methods[0]), Find(method));
                    if (a != b)
                        leader[string.CompareOrdinal(a, b) < 0 ? b : a] = string.CompareOrdinal(a, b) < 0 ? a : b;
                }
            }

            foreach (var group in writes.GroupBy(w => Find(CalledMethods(w.Edges[0])[0])).OrderBy(g => g.Key, StringComparer.Ordinal))
            {
                var call = new FlowCall(group.SelectMany(w => w.Edges).SelectMany(CalledMethods).Distinct(StringComparer.Ordinal)
                    .OrderBy(m => m, StringComparer.Ordinal).ToList());
                foreach (var (component, kind, edges, relations) in group)
                {
                    call.Writes.Add(kind == HandOff.None ? NotFollowed(component, edges, relations) : new FlowWrite(component, edges, IsHub(component)));
                    if (kind == HandOff.None)
                        continue;

                    var link = new FlowLink(component, kind, edges, relations);
                    if ((kind & (HandOff.Request | HandOff.Event)) != 0)
                    {
                        Follow(step, link, call.HandOffs);
                        continue;
                    }

                    if (_expandedComponents.Contains(component.Id) || edges.All(e => e.Via is "Add" or "AddComponent"))
                        continue;
                    var taken = Takers(component, kind, relations, step.Node, edges);
                    if (kind.HasFlag(HandOff.Watched) ? taken.Takers.Count > 0 : taken.Takers.Any(t => Frame(new[] { step.Node }, t, link, Root) is { Frames: not null, Never: false }))
                        Follow(step, link, call.HandOffs, taken);
                }

                if (call.Writes.Count > 1 || call.HandOffs.Count > 0)
                    step.Calls.Add(call);
                else
                    step.NotFollowed.AddRange(call.Writes);
            }
        }

        /// <summary>A write listed, not followed; for a [Watched] component with reactors, why none of them takes it.</summary>
        FlowWrite NotFollowed(GraphNode component, IReadOnlyList<GraphEdge> edges, IReadOnlyList<string> relations)
        {
            if (!component.Attributes.Contains("Watched"))
                return new FlowWrite(component, edges, IsHub(component));
            var (_, skipped, unseen) = Reactors(component, relations, edges);
            return new FlowWrite(component, edges, IsHub(component), skipped, unseen);
        }

        /// <summary>The methods the system called in its own code that led to this edge: `called`, or the method `through` names.</summary>
        internal static IReadOnlyList<string> CalledMethods(GraphEdge edge) =>
            edge.Called != null ? edge.Called.Split(new[] { "; " }, StringSplitOptions.None) : new[] { edge.Through ?? "?" };

        /// <summary>
        /// The condition a set of writes happens under, for one line — the same rule as for the
        /// sites of one edge: none when any of them is unconditional, or when one is made in an
        /// if and another in its else (an AddX and a ReplaceX are two edges); otherwise their
        /// conditions, three named. Null when there is none.
        /// </summary>
        internal static string? ConditionOf(IEnumerable<GraphEdge> edges)
        {
            var conditions = new HashSet<string>(StringComparer.Ordinal);
            foreach (var edge in edges)
            {
                if (edge.Condition == null)
                    return null;
                conditions.Add(edge.Condition);
            }

            return conditions.Count == 0 ? null : Analysis.Guards.Format(conditions);
        }

        void Take(FlowLink link, IReadOnlyList<GraphNode> producers, int hop, int? since, List<FlowTaker> takers)
        {
            foreach (var taker in takers.OrderBy(t => Root == null ? int.MaxValue : Order(t.Node, Root) ?? int.MaxValue)
                         .ThenBy(t => t.Node.Label, StringComparer.Ordinal)
                         .ThenBy(t => t.Node.Id, StringComparer.Ordinal))
            {
                var frame = Frame(producers, taker, link, Root);
                var elsewhere = producers.Count == 0
                    ? new List<(string, FlowFrame)>()
                    : OtherRoots.Select(r => (Root: r, Frame: Frame(producers, taker, link, r)))
                        .Where(o => (o.Frame.Frames != null || o.Frame.Never) && !o.Frame.SameAs(frame))
                        .ToList();

                // [+N] adds up the frames from the step marked [+0]. Where the start gives nothing to
                // count from — a request a window creates, creators that disagree, a system no root
                // runs — its first takers are that step.
                var step = new FlowStep(taker.Node, hop)
                {
                    Takes = link.Component.Id,
                    Since = since != null && frame.Frames != null ? since + frame.Frames
                        : hop == 1 && (since == null || StartLink != null) ? 0
                        : (int?)null
                };

                // A taker that never sees it does nothing with it: listed, not walked.
                if (!frame.Never && _expandedSystems.Add(taker.Node.Id))
                {
                    step.Expanded = true;
                    _queue.Enqueue(step);
                }
                link.Takers.Add(new FlowTake(step, taker.Edges, frame, elsewhere, taker.Marker,
                    taker.Marker ? OtherTriggers(taker.Node, link.Component) : Array.Empty<GraphNode>()));
            }

            link.Ends.AddRange(Ends(link, producers.Count == 1 ? producers[0] : null, takers));
        }

        /// <summary>
        /// The frame against every producer: the start component may have several creators, and the
        /// frame is known only when they agree ("same frame after 8 creators, next frame after 4").
        /// </summary>
        FlowFrame Frame(IReadOnlyList<GraphNode> producers, FlowTaker taker, FlowLink link, string? root)
        {
            if (producers.Count == 0)
                return FlowFrame.Unknown(WhyNoProducer());

            // The start's changers both add and remove a [Watched] component; a reacting group
            // takes only the changes Reactors lets through (no add when it leaves the component out).
            if (taker.Marker && link == StartLink)
            {
                producers = producers.Where(p => Reactors(link.Component, Relations(p, link.Component), WritesOf(p, link.Component)).Reacting.Contains(taker.Node)).ToList();
                if (producers.Count == 0)
                    return FlowFrame.Unknown($"none of the changes under {(root == null ? "the root" : Node(root).Label)} is one it takes");
            }

            var frames = producers.Select(p => Frame(p, taker, link, root)).ToList();
            if (frames.All(f => f.SameAs(frames[0])))
                return frames[0];
            return FlowFrame.Unknown(string.Join(", ", frames
                .GroupBy(f => f.Never ? "never" : f.Frames switch { null => "unknown", 0 => "same frame", 1 => "next frame", var n => $"{n} frames later" })
                .OrderBy(g => g.Key == "unknown" ? 1 : 0)
                .Select(g => $"{g.Key} after {g.Count()} creator{(g.Count() == 1 ? string.Empty : "s")}")));
        }

        string WhyNoProducer()
        {
            if (Root == null)
                return "no root feature runs it";
            if (StartStep != null)
                return $"{Start.Label} {NotRunning(Start, Root)}";
            var creators = Creators.Select(e => Node(e.Source)).Distinct().ToList();
            if (creators.Count == 0)
                return "nothing in the code creates it";
            var outside = creators.FirstOrDefault(c => c.Kind != NodeKind.System);
            return outside != null
                ? $"created by {outside.Label}, outside the systems"
                : $"{creators[0].Label} {NotRunning(creators[0], Root)}";
        }

        // -- hand-offs and takers --------------------------------------------------------------

        HandOff Kind(GraphNode component, IReadOnlyList<string> relations, IReadOnlyList<GraphEdge> writes)
        {
            var kind = HandOff.None;
            if (component.Attributes.Contains("Watched") && Reactors(component, relations, writes).Reacting.Count > 0)
                kind |= HandOff.Watched;
            if (relations.Contains(Relation.Creates))
            {
                // By name when the project follows the convention, otherwise by what happens to the entity.
                if (component.MessageRole == "request") kind |= HandOff.Request;
                else if (component.MessageRole == "event") kind |= HandOff.Event;
                if (component.TransientRemovedBy.Count > 0 || component.Attributes.Contains("Cleanup"))
                    kind |= HandOff.Transient;
            }
            return kind;
        }

        static int KindRank(HandOff kind) =>
            kind.HasFlag(HandOff.Request) ? 0 : kind.HasFlag(HandOff.Watched) ? 1 : kind.HasFlag(HandOff.Event) ? 2 : kind == HandOff.None ? 4 : 3;

        List<GraphEdge> WritesOf(GraphNode system, GraphNode component) => Out(system.Id)
            .Where(e => e.Target == component.Id && e.Relation is Relation.Creates or Relation.Removes)
            .ToList();

        List<string> Relations(GraphNode system, GraphNode component) => Out(system.Id)
            .Where(e => e.Target == component.Id && e.Relation is Relation.Creates or Relation.Removes)
            .Select(e => e.Relation)
            .Distinct()
            .OrderBy(r => r == Relation.Creates ? 0 : 1)
            .ToList();

        /// <summary>
        /// The systems reacting to a [Watched] component's marker that take this change. The
        /// marker is set on add and remove alike, but a group leaving the component out (NoneOf)
        /// takes the entity only once it is gone, and one requiring it only while it is there: the
        /// four systems reacting to UnpaidChanged on a 2000-file project all leave Unpaid out, and
        /// a factory adding Unpaid to new furniture reached none of them. The graph keeps no group,
        /// so a system with a second group is skipped too; the answer counts them (Skipped).
        ///
        /// Nor does a system whose group requires what the written entity never gets: furniture
        /// is never a cat, so the cat animation reacting to MovingChanged never sees the furniture
        /// factory clear Moving. The build works that out (graph.json `unseen_by`, on the write);
        /// a reactor is left out when every one of <paramref name="writes"/> says so (Unseen).
        /// </summary>
        (List<GraphNode> Reacting, int Skipped, List<FlowUnseen> Unseen) Reactors(GraphNode component, IReadOnlyList<string> relations,
            IReadOnlyList<GraphEdge> writes)
        {
            var all = In(component.Id)
                .Where(e => e.Relation == Relation.Reacts)
                .Select(e => Node(e.Source))
                .Where(n => n.Kind == NodeKind.System && !n.Generated)
                .Distinct()
                .ToList();
            var addOnly = relations.Count == 1 && relations[0] == Relation.Creates;
            var removeOnly = relations.Count == 1 && relations[0] == Relation.Removes;
            var taking = all.Where(n => !(addOnly && Filters(n, component)) && !(removeOnly && Requires(n, component))).ToList();

            var unseen = new Dictionary<string, string>(StringComparer.Ordinal);
            if (writes.Count > 0 && writes.All(w => w.UnseenBy.Count > 0))
            {
                foreach (var u in writes[0].UnseenBy)
                    if (writes.All(w => w.UnseenBy.Any(x => x.System == u.System)))
                        unseen[u.System] = u.Needs;
            }

            var reacting = taking.Where(n => !unseen.ContainsKey(n.Id)).ToList();
            var missed = taking.Where(n => unseen.ContainsKey(n.Id))
                .Select(n => new FlowUnseen(n, _graph.Node(unseen[n.Id]) is { } needs ? needs.Label : unseen[n.Id]))
                .ToList();
            return (reacting, all.Count - taking.Count, missed);
        }

        /// <summary>
        /// What else a reacting system takes: the markers of the other [Watched] components it
        /// reacts to. A system that never sees this change may start on another one — the cooking
        /// animation misses the factory's CookingChanged and plays once the view adds
        /// ProductStandAnimator — so "never sees it" alone read like a bug where it is none.
        /// </summary>
        IReadOnlyList<GraphNode> OtherTriggers(GraphNode system, GraphNode component) => Out(system.Id)
            .Where(e => e.Relation == Relation.Reacts && e.Target != component.Id)
            .OrderBy(e => e.Confidence == Confidence.Extracted ? 0 : 1)
            .ThenBy(e => e.SourceFile, StringComparer.Ordinal)
            .ThenBy(e => e.Line)
            .Select(e => Node(e.Target))
            .Distinct()
            .ToList();

        /// <summary>
        /// A hub (Id, Event, Ready, a position) is written next to nearly everything; following it
        /// would turn one flow into the project. Same measure as query: distinct neighbours over
        /// edges written in the code.
        /// </summary>
        bool IsHub(GraphNode component)
        {
            if (!_written.TryGetValue(component.Id, out var written))
            {
                written = In(component.Id).Concat(Out(component.Id))
                    .Where(e => e.Confidence == Confidence.Extracted)
                    .Select(e => e.Source == component.Id ? e.Target : e.Source)
                    .Distinct(StringComparer.Ordinal)
                    .Count();
                _written[component.Id] = written;
            }
            return written > _hubThreshold;
        }

        /// <summary>
        /// The systems a hand-off goes to: the readers of a request, event or transient component
        /// (the creation is what they wait for), the systems reacting to a [Watched] component's
        /// marker that take this kind of change (<see cref="Reactors"/>). Generated systems are
        /// endings, not takers; so is the producer itself.
        /// </summary>
        (List<FlowTaker> Takers, int Skipped, List<FlowUnseen> Unseen) Takers(GraphNode component, HandOff kind, IReadOnlyList<string> relations,
            GraphNode? producer, IReadOnlyList<GraphEdge> writes)
        {
            var reads = (kind & (HandOff.Request | HandOff.Event | HandOff.Transient | HandOff.State)) != 0;
            var (reactors, skipped, unseen) = kind.HasFlag(HandOff.Watched) ? Reactors(component, relations, writes) : (new List<GraphNode>(), 0, new List<FlowUnseen>());
            var reacting = new HashSet<string>(reactors.Select(r => r.Id), StringComparer.Ordinal);
            var incoming = In(component.Id);
            var takers = incoming
                .Where(e => reads && e.Relation == Relation.Reads || e.Relation == Relation.Reacts && reacting.Contains(e.Source))
                .GroupBy(e => e.Source, StringComparer.Ordinal)
                .Select(g => Node(g.Key))
                .Where(n => n.Kind == NodeKind.System && !n.Generated && n.Id != producer?.Id)
                .Select(n =>
                {
                    var reacts = reacting.Contains(n.Id);
                    var edges = incoming
                        .Where(e => e.Source == n.Id && (reads && e.Relation == Relation.Reads || reacts && e.Relation == Relation.Reacts ||
                                                         e.Relation is Relation.Removes or Relation.Destroys))
                        .OrderBy(e => e.Relation is Relation.Reads or Relation.Reacts ? 0 : 1)
                        .ThenBy(e => e.Confidence == Confidence.Extracted ? 0 : 1)
                        .ThenBy(e => e.Line)
                        .ToList();
                    return new FlowTaker(n, edges, reacts);
                })
                .ToList();
            return (takers, skipped, unseen.Where(u => u.System.Id != producer?.Id).ToList());
        }

        /// <summary>
        /// Where a hand-off ends: who removes or destroys it besides its takers, what clears a
        /// [Watched] marker, generated readers, readers that are no systems (a view, a window),
        /// or — for a request or event — that nothing does.
        /// </summary>
        IEnumerable<FlowEnd> Ends(FlowLink link, GraphNode? producer, IReadOnlyList<FlowTaker> takers)
        {
            var takerIds = new HashSet<string>(takers.Select(t => t.Node.Id), StringComparer.Ordinal);
            var incoming = In(link.Component.Id);
            var ends = new List<FlowEnd>();

            if (link.Kind.HasFlag(HandOff.Watched))
                foreach (var cleaner in incoming.Where(e => e.Relation == Relation.Reacts && Node(e.Source).Generated).GroupBy(e => e.Source))
                    ends.Add(new FlowEnd(FlowEnd.Cleared, Node(cleaner.Key), cleaner.ToList()));

            if (link.Kind == HandOff.Watched)
                return ends;

            var removers = incoming
                .Where(e => e.Relation is Relation.Removes or Relation.Destroys && !takerIds.Contains(e.Source) && e.Source != producer?.Id)
                .GroupBy(e => e.Source, StringComparer.Ordinal)
                .Select(g => g.OrderBy(e => e.Confidence == Confidence.Extracted ? 0 : 1).ThenBy(e => e.Relation == Relation.Destroys ? 0 : 1).ToList())
                .OrderBy(g => g[0].Confidence == Confidence.Extracted ? 0 : 1)
                .ThenBy(g => Root == null ? int.MaxValue : Order(Node(g[0].Source), Root) ?? int.MaxValue)
                .ThenBy(g => g[0].Source, StringComparer.Ordinal);
            foreach (var edges in removers)
                ends.Add(new FlowEnd(edges[0].Relation == Relation.Destroys ? FlowEnd.Destroyed : FlowEnd.Removed, Node(edges[0].Source), edges));

            // A generated cleanup reads what it removes; it is listed once, as the remover.
            var listed = new HashSet<string>(ends.Select(e => e.By.Id), StringComparer.Ordinal);
            foreach (var reader in incoming.Where(e => e.Relation == Relation.Reads && !listed.Contains(e.Source) && Node(e.Source) is { Generated: true, Kind: NodeKind.System })
                         .GroupBy(e => e.Source))
                ends.Add(new FlowEnd(FlowEnd.GeneratedReader, Node(reader.Key), reader.ToList()));

            // A query class reading it is already counted in the systems that call it (their
            // inferred reads name it in `through`); a type nobody calls that way — a window, a
            // view — is a reader the walk does not otherwise show.
            var throughs = incoming.Where(e => e.Confidence == Confidence.Inferred && e.Through != null).Select(e => e.Through!).ToList();
            var outside = incoming
                .Where(e => e.Relation == Relation.Reads && e.Confidence == Confidence.Extracted)
                .Select(e => Node(e.Source))
                .Where(n => n.Kind == NodeKind.Type && !throughs.Any(t => t.StartsWith(n.Label + ".", StringComparison.Ordinal) || t.Contains(" " + n.Label + ".")))
                .Distinct()
                .OrderBy(n => n.Label, StringComparer.Ordinal)
                .ToList();
            if (outside.Count > 0)
                ends.Add(new FlowEnd(FlowEnd.Outside, outside[0], Array.Empty<GraphEdge>(), outside));

            var removed = incoming.Any(e => e.Relation is Relation.Removes or Relation.Destroys);
            if (!removed && (link.Kind & (HandOff.Request | HandOff.Event)) != 0)
                ends.Add(new FlowEnd(FlowEnd.Nothing, link.Component, Array.Empty<GraphEdge>()));
            return ends;
        }

        // -- frames ----------------------------------------------------------------------------

        public static int? Order(GraphNode node, string root) => node.Placements.FirstOrDefault(p => p.Root == root)?.Order;

        /// <summary>
        /// Where a system runs in one frame of <paramref name="root"/>. A root's Execute() runs every
        /// execute system in registration order and its Cleanup() then runs every cleanup system,
        /// so a cleanup system ordered #5 still runs after an execute system ordered #300. Null
        /// when it does not run there, or runs only on Initialize or TearDown.
        /// </summary>
        static FlowTime? Slot(GraphNode node, string root)
        {
            var order = Order(node, root);
            var phase = node.Interfaces.Count == 0 || node.Interfaces.Contains("Execute") ? 0
                : node.Interfaces.Contains("Cleanup") ? 1
                : (int?)null;
            return order == null || phase == null ? null : new FlowTime(0, phase.Value, order.Value);
        }

        /// <summary>Why a system has no slot under the root, said of the system.</summary>
        string NotRunning(GraphNode node, string root) =>
            node.Kind != NodeKind.System ? "is no system"
            : node.Placements.Count == 0 ? "is in no feature"
            : Order(node, root) != null ? $"runs only on {string.Join("/", node.Interfaces)}"
            : $"runs under {string.Join(", ", node.Placements.Select(x => Node(x.Root).Label).Distinct())}, not {Node(root).Label}";

        /// <summary>
        /// When <paramref name="taker"/> first sees what <paramref name="producer"/> wrote, both
        /// running under <paramref name="root"/>: the frame the producer runs in is 0, a system
        /// running after it runs in that frame, one before it in the next. A gate the taker's
        /// group requires moves that to after the gate's setter; a system taking the component
        /// off before the taker runs means it may never see it.
        /// </summary>
        FlowFrame Frame(GraphNode producer, FlowTaker taker, FlowLink link, string? root)
        {
            if (root == null)
                return FlowFrame.Unknown("no root feature runs it");
            var component = link.Component;
            if (Slot(producer, root) is not { } from)
                return FlowFrame.Unknown($"{producer.Label} {NotRunning(producer, root)}");
            if (Slot(taker.Node, root) is not { } slot)
                return FlowFrame.Unknown(NotRunning(taker.Node, root));

            var arrival = Arrival(taker.Node, slot, from, producer, component, taker.Marker, root);
            if (arrival.Unknown != null)
                return arrival.Unknown;

            foreach (var (remover, certain, verb) in Removers(component, taker.Marker))
            {
                if (remover.Id == taker.Node.Id || remover.Id == producer.Id || Slot(remover, root) is not { } at)
                    continue;
                var removal = Arrival(remover, at, from, producer, component, taker.Marker, root);
                if (removal.Time is { } removed && removed.CompareTo(arrival.Time!.Value) < 0)
                {
                    var what = taker.Marker ? component.Label + "Changed" : "it";
                    return certain
                        ? FlowFrame.NeverSeen($"{remover.Label} #{at.Order} {verb}s {what} first")
                        : FlowFrame.Unknown($"{remover.Label} #{at.Order} may {verb} {what} first");
                }
            }

            return new FlowFrame(arrival.Time!.Value.Frame, arrival.Gate, arrival.GateOrder);
        }

        /// <summary>The first run of <paramref name="system"/> (at <paramref name="slot"/>) that sees the component: after its gates are set.</summary>
        (FlowTime? Time, FlowFrame? Unknown, FlowGate? Gate, int GateOrder) Arrival(
            GraphNode system, FlowTime slot, FlowTime from, GraphNode producer, GraphNode component, bool marker, string root)
        {
            var ready = from;
            FlowGate? gate = null;
            var gateOrder = 0;

            // Gates hold the component back, not its [Watched] marker: the change sets the marker
            // on an entity that already carries what the reacting group needs, or it never would.
            if (marker)
                return (ready.Next(slot), null, gate, gateOrder);

            foreach (var g in Gates(component).Where(g => Requires(system, g.Component)))
            {
                if (g.Partial)
                    return (null, FlowFrame.Unknown($"waits for {g.Component.Label}, which only some {component.Label} get later"), null, 0);
                var setter = g.Setter.Id == producer.Id ? from : Slot(g.Setter, root);
                if (setter == null)
                    return (null, FlowFrame.Unknown($"waits for {g.Component.Label}; {g.Setter.Label} {NotRunning(g.Setter, root)}"), null, 0);
                var set = g.Setter.Id == producer.Id ? from : from.Next(setter.Value);
                if (gate == null || set.CompareTo(ready) > 0)
                {
                    ready = set.CompareTo(ready) > 0 ? set : ready;
                    gate = g;
                    gateOrder = setter.Value.Order;
                }
            }

            // What the system's groups require and the producer's new entity is made without
            // (graph.json `waits`): it sees the entity once a system adds that, at the earliest
            // after that system's next run. Added after an await, outside the systems, or by
            // systems after which it would see it in different frames, and no frame is certain.
            foreach (var wait in WaitsOf(producer, component, system))
            {
                var needs = wait.Component.Label;
                if (wait.Added != null)
                    return (null, FlowFrame.Unknown($"waits for {needs}, added {wait.Added}"), null, 0);

                int? seen = null;
                FlowTime? latest = null;
                GraphNode? by = null;
                var order = 0;
                foreach (var setter in wait.Setters)
                {
                    var at = setter.Id == producer.Id ? from : Slot(setter, root) is { } s ? from.Next(s) : (FlowTime?)null;
                    if (at == null)
                        return (null, FlowFrame.Unknown($"waits for {needs}; {setter.Label} {NotRunning(setter, root)}"), null, 0);
                    var frame = (at.Value.CompareTo(ready) > 0 ? at.Value : ready).Next(slot).Frame;
                    if (seen != null && seen != frame)
                        return (null, FlowFrame.Unknown($"waits for {needs}; the frame depends on which of {wait.Setters.Count} systems adds it"), null, 0);
                    seen = frame;
                    if (latest == null || at.Value.CompareTo(latest.Value) > 0)
                    {
                        latest = at;
                        by = setter;
                        order = setter.Id == producer.Id ? from.Order : Slot(setter, root)!.Value.Order;
                    }
                }

                if (latest is { } added && added.CompareTo(ready) > 0)
                {
                    ready = added;
                    gate = new FlowGate(wait.Component, by!, false, wait.Setters.Count - 1);
                    gateOrder = order;
                }
            }

            return (ready.Next(slot), null, gate, gateOrder);
        }

        /// <summary>
        /// What <paramref name="system"/> waits for on the entities <paramref name="producer"/>
        /// makes the component on: what every one of its creates of it records (graph.json
        /// `waits`), since the system may see it through any of them. Added after an await or
        /// outside the systems when one of them says so; otherwise every setter any names.
        /// </summary>
        List<FlowWait> WaitsOf(GraphNode producer, GraphNode component, GraphNode system)
        {
            var key = producer.Id + "\u0001" + component.Id + "\u0001" + system.Id;
            if (_waits.TryGetValue(key, out var known))
                return known;

            var waits = new List<FlowWait>();
            var creates = WritesOf(producer, component).Where(e => e.Relation == Relation.Creates).ToList();
            if (creates.Count > 0 && creates.All(e => e.Waits.Count > 0))
            {
                foreach (var needs in creates[0].Waits.Where(w => w.System == system.Id).Select(w => w.Needs).Distinct(StringComparer.Ordinal))
                {
                    var all = creates.Select(e => e.Waits.FirstOrDefault(w => w.System == system.Id && w.Needs == needs)).ToList();
                    if (all.Any(w => w == null) || _graph.Node(needs) is not { } node)
                        continue;
                    var added = all.Select(w => w!.Added).Where(a => a != null)
                        .OrderBy(a => a == "after an await" ? 0 : a == "after a yield" ? 1 : 2)
                        .FirstOrDefault();
                    var setters = added != null
                        ? new List<GraphNode>()
                        : all.SelectMany(w => w!.AddedBy).Distinct(StringComparer.Ordinal).Select(id => _graph.Node(id)).OfType<GraphNode>().ToList();
                    if (added != null || setters.Count > 0)
                        waits.Add(new FlowWait(node, added, setters));
                }
            }

            _waits[key] = waits;
            return waits;
        }

        /// <summary>
        /// Who takes the component off, and whether for sure. For a [Watched] marker: the generated
        /// cleanup, which clears it on every entity. Otherwise systems removing or destroying it in
        /// their own code — for sure when generated or when they take it off every entity
        /// (<see cref="GraphNode.TransientRemovedBy"/>) — and a generic destroyer credited through
        /// the entity's shape (Event + Ready), which destroys every such entity once it is ready.
        /// </summary>
        IEnumerable<(GraphNode Remover, bool Certain, string Verb)> Removers(GraphNode component, bool marker)
        {
            var incoming = In(component.Id);
            if (marker)
                return incoming.Where(e => e.Relation == Relation.Reacts && Node(e.Source).Generated)
                    .Select(e => (Node(e.Source), true, "clear"))
                    .Distinct();

            return incoming
                .Where(e => e.Relation is Relation.Removes or Relation.Destroys &&
                            (e.Confidence == Confidence.Extracted || e.Through?.StartsWith("created with ", StringComparison.Ordinal) == true))
                .Select(e =>
                {
                    var node = Node(e.Source);
                    var certain = e.Confidence == Confidence.Inferred || node.Generated || component.TransientRemovedBy.Contains(node.Id);
                    return (node, certain, e.Relation == Relation.Destroys ? "destroy" : "remove");
                })
                .Distinct();
        }

        bool Requires(GraphNode system, GraphNode component) =>
            Out(system.Id).Any(e => e.Target == component.Id && e.Relation == Relation.Reads && e.Via is "AllOf" or "Matcher");

        bool Filters(GraphNode system, GraphNode component) =>
            Out(system.Id).Any(e => e.Target == component.Id && e.Relation == Relation.Filters);

        /// <summary>
        /// Components systems add to the component's entities after they are created, read from
        /// the `through` of its inferred destroys: "created with Event + X; Ready by
        /// MarkEventsReadySystem" says every X is created with Event and gets Ready from
        /// MarkEventsReadySystem. A gate named for some of the shapes X is created with, not all,
        /// is partial: whether this X gets it later is not known.
        /// </summary>
        List<FlowGate> Gates(GraphNode component)
        {
            if (_gates.TryGetValue(component.Id, out var known))
                return known;

            var shapes = In(component.Id)
                .Where(e => e.Relation == Relation.Destroys && e.Confidence == Confidence.Inferred && e.Through?.StartsWith("created with ", StringComparison.Ordinal) == true)
                .Select(e => e.Through!)
                .Distinct()
                .Select(ParseGates)
                .ToList();
            var gates = shapes.SelectMany(s => s)
                .GroupBy(g => (g.Component.Id, g.Setter.Id))
                .Select(g => new FlowGate(g.First().Component, g.First().Setter, shapes.Any(s => !s.Any(x => x.Component.Id == g.Key.Item1 && x.Setter.Id == g.Key.Item2))))
                .ToList();
            _gates[component.Id] = gates;
            return gates;
        }

        List<(GraphNode Component, GraphNode Setter)> ParseGates(string through)
        {
            var gates = new List<(GraphNode, GraphNode)>();
            var semicolon = through.IndexOf("; ", StringComparison.Ordinal);
            if (semicolon < 0)
                return gates;
            foreach (var part in through.Substring(semicolon + 2).Split(new[] { ", " }, StringSplitOptions.RemoveEmptyEntries))
            {
                var by = part.IndexOf(" by ", StringComparison.Ordinal);
                if (by <= 0)
                    continue;
                var label = part.Substring(0, by);
                var setterLabel = part.Substring(by + 4);
                var component = _graph.Nodes.FirstOrDefault(n => n.Kind == NodeKind.Component && n.Label == label);
                if (component == null)
                    continue;
                var setter = In(component.Id)
                    .Where(e => e.Relation == Relation.Creates && Node(e.Source).Label == setterLabel)
                    .Select(e => Node(e.Source))
                    .FirstOrDefault();
                if (setter != null)
                    gates.Add((component, setter));
            }
            return gates;
        }

        // -- order for reading ------------------------------------------------------------------

        /// <summary>
        /// The main chain first: under a system, hand-offs that lead on before those that end with
        /// their takers, requests before [Watched] changes before events; under a hand-off, takers
        /// that hand something on before references, before takers that end there (a sound, a
        /// statistic) — each in the order they see it.
        /// </summary>
        void Arrange()
        {
            var steps = new List<FlowStep>();
            if (StartStep != null)
                Collect(StartStep, steps);
            else
                foreach (var take in StartLink!.Takers)
                    Collect(take.Step, steps);
            if (StartLink != null)
                SortTakers(StartLink);

            foreach (var step in steps)
            {
                foreach (var link in step.AllHandOffs)
                    SortTakers(link);
                step.HandOffs.Sort(CompareHandOffs);
                foreach (var call in step.Calls)
                    call.HandOffs.Sort(CompareHandOffs);

                // Calls that lead on first, then by name.
                step.Calls.Sort((a, b) =>
                {
                    var c = (a.HandOffs.Any(h => h.LeadsOn) ? 0 : 1).CompareTo(b.HandOffs.Any(h => h.LeadsOn) ? 0 : 1);
                    if (c != 0) return c;
                    c = (a.HandOffs.Count > 0 ? 0 : 1).CompareTo(b.HandOffs.Count > 0 ? 0 : 1);
                    return c != 0 ? c : string.CompareOrdinal(a.Method, b.Method);
                });
            }
        }

        static int CompareHandOffs(FlowLink a, FlowLink b)
        {
            var c = (a.Expanded ? 0 : 1).CompareTo(b.Expanded ? 0 : 1);
            if (c != 0) return c;
            c = (a.LeadsOn ? 0 : 1).CompareTo(b.LeadsOn ? 0 : 1);
            if (c != 0) return c;
            c = KindRank(a.Kind).CompareTo(KindRank(b.Kind));
            if (c != 0) return c;
            c = a.Writes[0].Line.CompareTo(b.Writes[0].Line);
            return c != 0 ? c : string.CompareOrdinal(a.Component.Label, b.Component.Label);
        }

        static void Collect(FlowStep step, List<FlowStep> steps)
        {
            if (!step.Expanded)
                return;
            steps.Add(step);
            foreach (var link in step.AllHandOffs)
            foreach (var take in link.Takers)
                Collect(take.Step, steps);
        }

        void SortTakers(FlowLink link) => link.Takers.Sort((a, b) =>
        {
            var c = Rank(a).CompareTo(Rank(b));
            if (c != 0) return c;
            c = (a.Frame.Frames ?? int.MaxValue).CompareTo(b.Frame.Frames ?? int.MaxValue);
            if (c != 0) return c;
            c = When(a).CompareTo(When(b));
            return c != 0 ? c : string.CompareOrdinal(a.Step.Node.Label, b.Step.Node.Label);

            FlowTime When(FlowTake t) => (Root == null ? null : Slot(t.Step.Node, Root)) ?? new FlowTime(0, int.MaxValue, int.MaxValue);

            static int Rank(FlowTake t) => t.Frame.Never ? 3 : !t.Step.Expanded ? 1 : t.Step.HandsOn ? 0 : 2;
        });
    }

    /// <summary>A system in the flow. The first occurrence of a system is expanded; later ones refer to it.</summary>
    internal sealed class FlowStep
    {
        public readonly GraphNode Node;

        /// <summary>System hops from the start: the start system is 0, the takers of the start component 1.</summary>
        public readonly int Hop;

        public bool Expanded;

        /// <summary>The id of the component the step takes: its taker line already says what it does to it.</summary>
        public string? Takes;

        /// <summary>Frames since the step marked [+0]; null once a frame on the way is unknown.</summary>
        public int? Since;

        /// <summary>What it writes that others take, followed.</summary>
        public readonly List<FlowLink> HandOffs = new List<FlowLink>();

        /// <summary>What it writes that is state or a hub: listed, not followed.</summary>
        public readonly List<FlowWrite> NotFollowed = new List<FlowWrite>();

        /// <summary>What it writes only through methods it calls, one entry per method.</summary>
        public readonly List<FlowCall> Calls = new List<FlowCall>();

        public FlowStep(GraphNode node, int hop)
        {
            Node = node;
            Hop = hop;
        }

        /// <summary>Its hand-offs: its own, then those through the methods it calls.</summary>
        public IEnumerable<FlowLink> AllHandOffs => HandOffs.Concat(Calls.SelectMany(c => c.HandOffs));

        /// <summary>It hands something on, in its own code or through a call.</summary>
        public bool HandsOn => HandOffs.Count > 0 || Calls.Any(c => c.HandOffs.Count > 0);
    }

    /// <summary>
    /// A method a system calls in its own code (a factory, a helper), and what the system writes
    /// through it: every component on one line, the requests and events among them followed.
    /// </summary>
    internal sealed class FlowCall
    {
        /// <summary>"FurnitureFactory.CreateFurniture"; several when they write a component in common.</summary>
        public readonly IReadOnlyList<string> Methods;

        public readonly List<FlowWrite> Writes = new List<FlowWrite>();

        /// <summary>The writes through it that are followed.</summary>
        public readonly List<FlowLink> HandOffs = new List<FlowLink>();

        public FlowCall(IReadOnlyList<string> methods) => Methods = methods;

        public string Method => string.Join(", ", Methods);

        public IEnumerable<GraphEdge> Edges => Writes.SelectMany(w => w.Edges);
    }

    /// <summary>A component a step hands off, and the systems taking it from there.</summary>
    internal sealed class FlowLink
    {
        public readonly GraphNode Component;
        public readonly HandOff Kind;

        /// <summary>The producer's writes of it, extracted first; empty for the start.</summary>
        public readonly IReadOnlyList<GraphEdge> Writes;

        /// <summary>creates, removes or both: decides which [Watched] reactions apply.</summary>
        public readonly IReadOnlyList<string> Relations;

        public bool Expanded;

        /// <summary>At the last hop: takers --depth did not follow.</summary>
        public int Beyond;

        /// <summary>Systems reacting to the marker that this kind of change does not reach (<see cref="FlowWalk"/>'s Reactors).</summary>
        public int Skipped;

        /// <summary>Systems reacting to the marker whose group the written entity can never match.</summary>
        public readonly List<FlowUnseen> Unseen = new List<FlowUnseen>();

        public readonly List<FlowTake> Takers = new List<FlowTake>();
        public readonly List<FlowEnd> Ends = new List<FlowEnd>();

        public FlowLink(GraphNode component, HandOff kind, IReadOnlyList<GraphEdge> writes, IReadOnlyList<string> relations)
        {
            Component = component;
            Kind = kind;
            Writes = writes;
            Relations = relations;
        }

        /// <summary>Some taker hands something on in turn.</summary>
        public bool LeadsOn => Takers.Any(t => t.Step.Expanded && t.Step.HandsOn);
    }

    /// <summary>One taker of a hand-off, and when it sees it.</summary>
    internal sealed class FlowTake
    {
        public readonly FlowStep Step;

        /// <summary>Its reads (or reactions) of the component, then its removes and destroys of it.</summary>
        public readonly IReadOnlyList<GraphEdge> Edges;

        public readonly FlowFrame Frame;

        /// <summary>Other roots of the start where the frame differs.</summary>
        public readonly IReadOnlyList<(string Root, FlowFrame Frame)> Elsewhere;

        /// <summary>Reacts to the [Watched] marker rather than reading the component.</summary>
        public readonly bool Marker;

        /// <summary>For a reaction: the other [Watched] components the system reacts to.</summary>
        public readonly IReadOnlyList<GraphNode> OtherTriggers;

        public FlowTake(FlowStep step, IReadOnlyList<GraphEdge> edges, FlowFrame frame, IReadOnlyList<(string, FlowFrame)> elsewhere, bool marker,
            IReadOnlyList<GraphNode> otherTriggers)
        {
            Step = step;
            Edges = edges;
            Frame = frame;
            Elsewhere = elsewhere;
            Marker = marker;
            OtherTriggers = otherTriggers;
        }
    }

    internal sealed class FlowTaker
    {
        public readonly GraphNode Node;
        public readonly List<GraphEdge> Edges;
        public readonly bool Marker;

        public FlowTaker(GraphNode node, List<GraphEdge> edges, bool marker)
        {
            Node = node;
            Edges = edges;
            Marker = marker;
        }
    }

    /// <summary>A write the walk lists without following it: state, or a hub.</summary>
    internal sealed class FlowWrite
    {
        public readonly GraphNode Component;
        public readonly IReadOnlyList<GraphEdge> Edges;
        public readonly bool Hub;

        /// <summary>For a [Watched] component: systems reacting to it that take only the other kind of change.</summary>
        public readonly int Skipped;

        /// <summary>For a [Watched] component: systems reacting to it whose group the written entity can never match.</summary>
        public readonly IReadOnlyList<FlowUnseen> Unseen;

        public FlowWrite(GraphNode component, IReadOnlyList<GraphEdge> edges, bool hub, int skipped = 0, IReadOnlyList<FlowUnseen>? unseen = null)
        {
            Component = component;
            Edges = edges;
            Hub = hub;
            Skipped = skipped;
            Unseen = unseen ?? Array.Empty<FlowUnseen>();
        }
    }

    /// <summary>A system reacting to a [Watched] component that never sees this write, and a component its group needs that the entity never gets.</summary>
    internal sealed class FlowUnseen
    {
        public readonly GraphNode System;
        public readonly string Needs;

        public FlowUnseen(GraphNode system, string needs)
        {
            System = system;
            Needs = needs;
        }
    }

    /// <summary>Where a hand-off ends.</summary>
    internal sealed class FlowEnd
    {
        public const string Destroyed = "destroyed", Removed = "removed", Cleared = "cleared", GeneratedReader = "generated", Outside = "outside", Nothing = "nothing";

        public readonly string Kind;
        public readonly GraphNode By;
        public readonly IReadOnlyList<GraphEdge> Edges;

        /// <summary>For <see cref="Outside"/>: every reader that is no system.</summary>
        public readonly IReadOnlyList<GraphNode> Readers;

        public FlowEnd(string kind, GraphNode by, IReadOnlyList<GraphEdge> edges, IReadOnlyList<GraphNode>? readers = null)
        {
            Kind = kind;
            By = by;
            Edges = edges;
            Readers = readers ?? Array.Empty<GraphNode>();
        }
    }

    /// <summary>A component another system adds to an entity after it is created, and that system.</summary>
    internal sealed class FlowGate
    {
        public readonly GraphNode Component;
        public readonly GraphNode Setter;

        /// <summary>Named for only some of the shapes the component is created with.</summary>
        public readonly bool Partial;

        /// <summary>Other systems adding it, after which the taker sees it in the same frame as after <see cref="Setter"/>.</summary>
        public readonly int More;

        public FlowGate(GraphNode component, GraphNode setter, bool partial, int more = 0)
        {
            Component = component;
            Setter = setter;
            Partial = partial;
            More = more;
        }
    }

    /// <summary>
    /// A component a taker's groups require that the producer's new entity is made without
    /// (graph.json `waits`): how it arrives, after an await or outside the systems, or the
    /// systems adding it.
    /// </summary>
    internal sealed class FlowWait
    {
        public readonly GraphNode Component;
        public readonly string? Added;
        public readonly IReadOnlyList<GraphNode> Setters;

        public FlowWait(GraphNode component, string? added, IReadOnlyList<GraphNode> setters)
        {
            Component = component;
            Added = added;
            Setters = setters;
        }
    }

    /// <summary>A point in execution under one root: the frame, the phase (0 Execute, 1 Cleanup), the order within it.</summary>
    internal readonly struct FlowTime : IComparable<FlowTime>
    {
        public readonly int Frame;
        public readonly int Phase;
        public readonly int Order;

        public FlowTime(int frame, int phase, int order)
        {
            Frame = frame;
            Phase = phase;
            Order = order;
        }

        /// <summary>The first run after this point of the system running at <paramref name="slot"/>'s phase and order.</summary>
        public FlowTime Next(FlowTime slot)
        {
            var later = slot.Phase > Phase || slot.Phase == Phase && slot.Order > Order;
            return new FlowTime(later ? Frame : Frame + 1, slot.Phase, slot.Order);
        }

        public int CompareTo(FlowTime other)
        {
            var c = Frame.CompareTo(other.Frame);
            if (c != 0) return c;
            c = Phase.CompareTo(other.Phase);
            return c != 0 ? c : Order.CompareTo(other.Order);
        }
    }

    /// <summary>When a taker sees a hand-off, relative to its producer.</summary>
    internal sealed class FlowFrame
    {
        /// <summary>0 the same frame, 1 the next…; null when unknown or never.</summary>
        public readonly int? Frames;

        public readonly bool Never;

        /// <summary>Why it is unknown, or why never.</summary>
        public readonly string? Why;

        /// <summary>The gate that decided it, if any, and where its setter runs.</summary>
        public readonly FlowGate? Gate;

        public readonly int GateOrder;

        public FlowFrame(int frames, FlowGate? gate, int gateOrder)
        {
            Frames = frames;
            Gate = gate;
            GateOrder = gateOrder;
        }

        FlowFrame(bool never, string why)
        {
            Never = never;
            Why = why;
        }

        public static FlowFrame Unknown(string why) => new FlowFrame(false, why);
        public static FlowFrame NeverSeen(string why) => new FlowFrame(true, why);

        public bool SameAs(FlowFrame other) => Frames == other.Frames && Never == other.Never && (Frames != null || Never == other.Never && Why == other.Why);
    }
}
