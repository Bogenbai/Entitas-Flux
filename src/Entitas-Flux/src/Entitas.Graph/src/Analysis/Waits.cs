using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.CodeAnalysis;

namespace Entitas.Graph.Analysis
{
    /// <summary>
    /// What a reader of a message waits for when the message is put on an entity just made
    /// without something the reader's group requires. RestoreReplacedFurnitureOnPlacementCancelledSystem
    /// builds furniture (with a view asset, without a View) and marks it TakeOutPending; the
    /// system taking that out requires View too, which arrives only once the view's asset is
    /// loaded, after an await. A flow said "same frame". The entity's components exist only
    /// while the graph is built, so the answer goes into graph.json, on the write
    /// (<see cref="GraphEdge.Waits"/>), and `flow` turns it into a frame.
    ///
    /// Sound before complete: a wait is recorded only when all of this is certain —
    /// <list type="bullet">
    /// <item>The write lands on an entity made in the same run: right there, by a factory, or
    /// passed to a helper by callers in the writing system's own reach that made it
    /// (<see cref="Creations.Resolve"/> with <c>fresh</c>). One of a group's, or one an index
    /// finds, may have got anything since it was made.</item>
    /// <item>Every creation it can come from is known, and none includes the component.</item>
    /// <item>The reader finds the message only through groups of its own requiring the
    /// component: every matcher of it sits in such a group, every other read of it is on the
    /// variable of a foreach over one, and nothing it calls reads it.</item>
    /// <item>The component can arrive later (it is no creation-only one: then the reader never
    /// sees the entity, and nothing is recorded) and the writer adds it nowhere itself — when
    /// it does, it may be what puts it on this entity, in the same run.</item>
    /// </list>
    /// How it arrives is decided per system adding it, and per way that system reaches the
    /// write (<see cref="Lateness"/>): only the systems that can add it to this entity count —
    /// not one whose loop runs over a group the entity can never match, its own loop's for a
    /// write it makes through calls. All of them reach it at once: the systems, one of which the
    /// reader waits for (<see cref="ReaderWait.AddedBy"/>). All of them only after an await or a
    /// yield, or a write in a type no system reaches: no frame can be told
    /// (<see cref="ReaderWait.Added"/>). Some at once and some late: no frame either, and
    /// <see cref="ReaderWait.LateBy"/> names the late ones.
    /// </summary>
    sealed class Waits
    {
        const string AfterAwait = "after an await", AfterYield = "after a yield", Outside = "outside the systems";

        readonly BuildContext _context;
        readonly Creations _creations;
        readonly Lateness _lateness;
        readonly Dictionary<string, List<Access>> _setters;
        readonly Dictionary<string, TypeInfo> _systems;
        readonly Dictionary<IMethodSymbol, SortedSet<string>> _runners = new Dictionary<IMethodSymbol, SortedSet<string>>(SymbolEqualityComparer.Default);
        readonly Dictionary<string, HashSet<IMethodSymbol>> _reach = new Dictionary<string, HashSet<IMethodSymbol>>(StringComparer.Ordinal);
        readonly Dictionary<string, Func<IMethodSymbol, bool>> _trees = new Dictionary<string, Func<IMethodSymbol, bool>>(StringComparer.Ordinal);
        readonly Dictionary<string, Arrival?> _arrivals = new Dictionary<string, Arrival?>(StringComparer.Ordinal);

        Waits(BuildContext context, Creations creations, Lateness lateness, List<Access> accesses)
        {
            _context = context;
            _creations = creations;
            _lateness = lateness;
            _setters = accesses.Where(a => a.Relation == Relation.Creates && a.Origin.Created == null)
                .GroupBy(a => a.Component.Name)
                .ToDictionary(g => g.Key, g => g.ToList(), StringComparer.Ordinal);
            _systems = context.Types.Values.Where(t => t.Kind == NodeKind.System).ToDictionary(t => t.Id, StringComparer.Ordinal);

            foreach (var system in _systems.Values)
            foreach (var method in creations.Reach(system))
            {
                if (!_runners.TryGetValue(method, out var runners))
                    _runners[method] = runners = new SortedSet<string>(StringComparer.Ordinal);
                runners.Add(system.Id);
            }
        }

        public static void Mark(BuildContext context, Creations creations, Lateness lateness, List<Access> accesses, HashSet<string> handOffs,
            Dictionary<string, GraphEdge> edges)
        {
            var writes = accesses.Where(a => a.Relation == Relation.Creates && handOffs.Contains(a.Component.Name)).ToList();
            var readers = Readers(accesses, handOffs, edges);
            if (writes.Count == 0 || readers.Count == 0)
                return;

            var waits = new Waits(context, creations, lateness, accesses);
            var byAccessor = writes.GroupBy(a => a.Accessor.Id).ToDictionary(g => g.Key, g => g.ToList(), StringComparer.Ordinal);
            var byMethod = writes.Where(a => a.Method != null && a.Accessor.Kind == NodeKind.Type)
                .GroupBy<Access, IMethodSymbol>(a => a.Method!, SymbolEqualityComparer.Default)
                .ToDictionary<IGrouping<IMethodSymbol, Access>, IMethodSymbol, List<Access>>(g => g.Key, g => g.ToList(), SymbolEqualityComparer.Default);

            // Per edge, the creations its writes land on (null once one of them is not an entity
            // made in the same run), the helpers that was followed through, and who writes.
            var landsOn = new Dictionary<string, SortedSet<string>?>(StringComparer.Ordinal);
            var through = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
            var written = new Dictionary<string, (string Component, TypeInfo Writer)>(StringComparer.Ordinal);
            foreach (var system in waits._systems.Values)
            {
                var reached = creations.Reach(system);
                var own = byAccessor.TryGetValue(system.Id, out var found) ? found : new List<Access>();
                if (own.Count == 0 && !reached.Any(byMethod.ContainsKey))
                    continue;

                var inTree = waits.InTree(system);
                var sites = own.Select(a => (Key: a.ToEdge(Confidence.Extracted, null).Key, Write: a))
                    .Concat(reached.Where(byMethod.ContainsKey).SelectMany(m => byMethod[m].Select(a =>
                        (Key: a.ToEdge(Confidence.Inferred, $"{m.ContainingType.Name}.{m.Name}", system.Id).Key, Write: a))));
                foreach (var (key, write) in sites)
                {
                    written[key] = (write.Component.Name, system);
                    if (landsOn.TryGetValue(key, out var keys) && keys == null)
                        continue;
                    var visited = new HashSet<(IMethodSymbol Method, int Parameter)>();
                    var resolved = creations.Resolve(write.Origin, inTree, visited, fresh: true);
                    if (resolved == null || resolved.Count == 0)
                    {
                        landsOn[key] = null;
                        continue;
                    }
                    if (keys == null)
                        landsOn[key] = keys = new SortedSet<string>(StringComparer.Ordinal);
                    keys.UnionWith(resolved);
                    if (!through.TryGetValue(key, out var helpers))
                        through[key] = helpers = new HashSet<string>(StringComparer.Ordinal);
                    helpers.UnionWith(visited.Select(v => v.Method.Name));
                }
            }

            foreach (var pair in landsOn)
            {
                if (pair.Value == null || !edges.TryGetValue(pair.Key, out var edge))
                    continue;
                var (component, writer) = written[pair.Key];
                if (!readers.TryGetValue(component, out var takers))
                    continue;
                var shapes = pair.Value.Select(creations.Shape).ToList();
                if (shapes.Any(s => s == null))
                    continue;

                foreach (var (reader, groups) in takers)
                {
                    if (reader.Id == writer.Id)
                        continue;
                    var found = waits.Of(reader, groups, component, writer, pair.Value, shapes!);
                    if (found != null)
                        edge.Waits.AddRange(found);
                }
                edge.Waits.Sort((a, b) =>
                {
                    var c = string.CompareOrdinal(a.System, b.System);
                    return c != 0 ? c : string.CompareOrdinal(a.Needs, b.Needs);
                });
            }

            // A helper also handed out as a delegate runs on whatever the delegate is given: the
            // entity its parameter was followed to is not known after all (as for unseen reactors).
            // A method adding the component that is handed out runs whenever it is called — at
            // a system's turn or not, on this entity while it is made or not — so a reader
            // waiting for that component keeps what it had. Looked for last, by name, once.
            var marked = edges.Values.Where(e => e.Waits.Count > 0).ToList();
            var setters = marked.SelectMany(e => e.Waits).Select(w => w.Needs).Distinct(StringComparer.Ordinal)
                .ToDictionary(id => id, id => waits.SetterMethods(id.Substring("component:".Length)), StringComparer.Ordinal);
            var handedOut = Creations.HandedOut(context, marked.Where(e => through.ContainsKey(e.Key)).SelectMany(e => through[e.Key])
                .Concat(setters.Values.SelectMany(s => s)));
            foreach (var edge in marked)
            {
                if (through.TryGetValue(edge.Key, out var helpers) && helpers.Overlaps(handedOut))
                {
                    edge.Waits.Clear();
                    continue;
                }
                var unsure = edge.Waits.Where(w => setters[w.Needs].Overlaps(handedOut)).Select(w => w.System).ToHashSet(StringComparer.Ordinal);
                edge.Waits.RemoveAll(w => unsure.Contains(w.System));
            }
        }

        /// <summary>Whether a method is the system's own or one it reaches through calls: whose callers count when a parameter is followed.</summary>
        Func<IMethodSymbol, bool> InTree(TypeInfo system)
        {
            if (_trees.TryGetValue(system.Id, out var known))
                return known;
            var inReach = new HashSet<IMethodSymbol>(_creations.Reach(system), SymbolEqualityComparer.Default);
            bool InTree(IMethodSymbol method) =>
                inReach.Contains(method) || SymbolEqualityComparer.Default.Equals(method.ContainingType?.OriginalDefinition, system.Symbol);
            _trees[system.Id] = InTree;
            return InTree;
        }

        /// <summary>The names of the methods adding the component to existing entities, but a system's Execute() and Cleanup(), which Entitas runs.</summary>
        HashSet<string> SetterMethods(string component) =>
            _setters.TryGetValue(component, out var setters)
                ? setters.Where(s => s.Method != null && !_context.RunsEveryFrame(s.Method)).Select(s => s.Method!.Name).ToHashSet(StringComparer.Ordinal)
                : new HashSet<string>(StringComparer.Ordinal);

        /// <summary>
        /// Per request, event or transient component, the systems reading it only through groups
        /// of their own — every matcher of it in a group assigned to a field or local, every other
        /// read of it on the variable of a foreach over one — with those groups. A system finding
        /// it any other way (an index, a unique, a query it calls) may see it on any entity.
        /// </summary>
        static Dictionary<string, List<(TypeInfo Reader, List<ISymbol> Groups)>> Readers(List<Access> accesses, HashSet<string> handOffs,
            Dictionary<string, GraphEdge> edges)
        {
            var inferred = new HashSet<(string, string)>(edges.Values
                .Where(e => e.Relation == Relation.Reads && e.Confidence == Confidence.Inferred)
                .Select(e => (e.Source, e.Target)));
            var readers = new Dictionary<string, List<(TypeInfo, List<ISymbol>)>>(StringComparer.Ordinal);
            foreach (var reads in accesses
                         .Where(a => a.Relation == Relation.Reads && a.Accessor.Kind == NodeKind.System && !a.Accessor.Generated && handOffs.Contains(a.Component.Name))
                         .GroupBy(a => (a.Component, a.Accessor)))
            {
                var (component, reader) = reads.Key;
                if (inferred.Contains((reader.Id, component.Id)))
                    continue;
                var groups = new List<ISymbol>();
                if (reads.Any(read => (read.Clause != null ? read.MatcherGroup : read.ReadGroup) == null))
                    continue;
                foreach (var group in reads.Select(read => read.Clause != null ? read.MatcherGroup! : read.ReadGroup!))
                    if (!groups.Contains(group, SymbolEqualityComparer.Default))
                        groups.Add(group);
                if (!readers.TryGetValue(component.Name, out var list))
                    readers[component.Name] = list = new List<(TypeInfo, List<ISymbol>)>();
                list.Add((reader, groups));
            }
            return readers;
        }

        /// <summary>
        /// What <paramref name="reader"/> waits for on an entity made at <paramref name="keys"/>:
        /// each component all its groups require that no creation includes. Null when it never
        /// sees the entity at all (a component only creations add, or one nothing can add to it):
        /// that is no wait, and nothing is said.
        /// </summary>
        List<ReaderWait>? Of(TypeInfo reader, List<ISymbol> groups, string component, TypeInfo writer, SortedSet<string> keys,
            List<HashSet<string>> shapes)
        {
            IEnumerable<string> required = _creations.Required(groups[0]);
            foreach (var group in groups.Skip(1))
                required = required.Intersect(_creations.Required(group), StringComparer.Ordinal);

            var waits = new List<ReaderWait>();
            foreach (var needs in required.Where(c => c != component).Distinct(StringComparer.Ordinal).OrderBy(c => c, StringComparer.Ordinal))
            {
                if (shapes.Any(s => s.Contains(needs)) || Adds(writer, needs))
                    continue;
                if (_creations.CreationOnly(needs) || ArrivalOf(needs, keys, shapes) is not { } arrival)
                    return null;
                var wait = new ReaderWait { System = reader.Id, Needs = "component:" + needs, Added = arrival.Added };
                wait.AddedBy.AddRange(arrival.By);
                wait.LateBy.AddRange(arrival.LateBy);
                waits.Add(wait);
            }
            return waits;
        }

        /// <summary>
        /// Whether the writer itself adds the component to an entity not made right there, in its
        /// own code or in any method it calls, however deep (a setup helper the factory's helper
        /// calls): maybe to this one, while making it.
        /// </summary>
        bool Adds(TypeInfo writer, string component)
        {
            if (!_setters.TryGetValue(component, out var setters))
                return false;
            if (!_reach.TryGetValue(writer.Id, out var reach))
                _reach[writer.Id] = reach = Calls(writer);
            return setters.Any(s => s.Accessor.Id == writer.Id || s.Method != null && reach.Contains(s.Method));
        }

        /// <summary>Every method of the project the system's code calls, through any number of calls.</summary>
        HashSet<IMethodSymbol> Calls(TypeInfo system)
        {
            var found = new HashSet<IMethodSymbol>(SymbolEqualityComparer.Default);
            if (!_creations.MethodsOf.TryGetValue(system.Symbol, out var roots))
                return found;
            var queue = new Queue<IMethodSymbol>(roots);
            while (queue.Count > 0)
            {
                if (!_creations.Callees.TryGetValue(queue.Dequeue(), out var further))
                    continue;
                foreach (var call in further)
                foreach (var method in _context.Dispatch(new[] { call.Callee }))
                    if (found.Add(method))
                        queue.Enqueue(method);
            }
            return found;
        }

        /// <summary>How a component reaches an entity: when (<see cref="ReaderWait.Added"/>), by whom at once, by whom late.</summary>
        sealed class Arrival
        {
            public readonly string? Added;
            public readonly List<string> By;
            public readonly List<string> LateBy;

            public Arrival(string? added, List<string> by, List<string> lateBy)
            {
                Added = added;
                By = by;
                LateBy = lateBy;
            }
        }

        /// <summary>
        /// How the component arrives on an entity made at <paramref name="keys"/>: every write of
        /// it on an existing entity that can be on this one, by the systems that can make it here,
        /// each with the ways it reaches the write (<see cref="Serving"/>). "after an await" or
        /// "after a yield" when some way is that, with the systems taking one named when others
        /// make it at once; "outside the systems" for a write in a type no system reaches;
        /// otherwise the systems making them. Null when there is none.
        /// </summary>
        Arrival? ArrivalOf(string component, SortedSet<string> keys, List<HashSet<string>> shapes)
        {
            var id = component + "\u0001" + string.Join("\u0001", keys);
            if (_arrivals.TryGetValue(id, out var known))
                return known;

            Arrival? arrival = null;
            if (_setters.TryGetValue(component, out var setters))
            {
                var bySystem = new Dictionary<string, int>(StringComparer.Ordinal);
                var outside = Ways.None;
                foreach (var setter in setters)
                {
                    if (setter.Group != null && !CanMatch(setter.Group, component, shapes))
                        continue;

                    if (setter.Accessor.Kind == NodeKind.System)
                    {
                        bySystem.TryGetValue(setter.Accessor.Id, out var had);
                        bySystem[setter.Accessor.Id] = had | (int)_lateness.Of(setter);
                    }
                    else if (setter.Accessor.Kind == NodeKind.Type && setter.Method != null && _runners.TryGetValue(setter.Method, out var runners))
                    {
                        foreach (var runner in runners)
                        {
                            var ways = Serving(_systems[runner], setter, component, shapes);
                            if (ways == Ways.None)
                                continue;
                            bySystem.TryGetValue(runner, out var had);
                            bySystem[runner] = had | (int)ways;
                        }
                    }
                    else
                    {
                        outside |= _lateness.Of(setter);
                    }
                }

                if (bySystem.Count > 0 || outside != Ways.None)
                {
                    var all = outside;
                    foreach (var ways in bySystem.Values)
                        all |= (Ways)ways;
                    var added = (all & Ways.Await) != 0 ? AfterAwait : (all & Ways.Yield) != 0 ? AfterYield : outside != Ways.None ? Outside : null;
                    var atOnce = Adding(bySystem, Ways.Sync);
                    if (added == null)
                        arrival = new Arrival(null, atOnce, new List<string>());
                    else if (added != Outside && (atOnce.Count > 0 || (outside & Ways.Sync) != 0))
                        arrival = new Arrival(added, atOnce, Adding(bySystem, Ways.Late));
                    else
                        arrival = new Arrival(added, new List<string>(), new List<string>());
                }
            }

            _arrivals[id] = arrival;
            return arrival;
        }

        /// <summary>The systems some of whose ways are of <paramref name="kinds"/>, in order.</summary>
        static List<string> Adding(Dictionary<string, int> bySystem, Ways kinds) =>
            bySystem.Keys.Where(id => ((Ways)bySystem[id] & kinds) != 0).OrderBy(id => id, StringComparer.Ordinal).ToList();

        /// <summary>
        /// The ways <paramref name="system"/> reaches a write a type makes, on the paths that can
        /// lead to this entity (made with <paramref name="shapes"/>); none when no path can. The
        /// entity written is the one the system passed in, unless the write's own is known to be
        /// another: a parameter followed back to the system's loops says which groups it comes
        /// from, and one followed anywhere else (an index, an entity made) leaves every path. A
        /// write on an entity not traced at all (a field: EntityBehaviour._entity) is taken to be
        /// the one the system passed: its own loop's group decides.
        /// </summary>
        Ways Serving(TypeInfo system, Access setter, string component, List<HashSet<string>> shapes)
        {
            if (!_lateness.From(system).TryGetValue(setter.Method!, out var paths))
                return Ways.None;

            var origin = setter.Origin;
            var byEntry = origin.Group == null && origin.Carries == null && origin.Created == null;
            if (origin.Method != null)
            {
                var traced = _creations.Groups(origin, InTree(system), new HashSet<(IMethodSymbol, int)>());
                if (traced is { Count: > 0 })
                {
                    if (!traced.Any(g => CanMatch(g, component, shapes)))
                        return Ways.None;
                }
                else
                {
                    byEntry = false;
                }
            }

            var ways = Ways.None;
            foreach (var path in paths)
                if (!byEntry || path.Entry == null || CanMatch(path.Entry, component, shapes))
                    ways |= path.Kind;
            return Lateness.Through(ways, setter.Late);
        }

        /// <summary>
        /// Whether an entity made with <paramref name="shapes"/> can ever be in the group: not
        /// when the group requires, besides the component arriving, one that only creations add
        /// and the entity was made without, or one nothing in the project adds at all.
        /// </summary>
        bool CanMatch(ISymbol group, string component, List<HashSet<string>> shapes) =>
            !_creations.Required(group).Any(r => r != component && (_creations.CreationOnly(r) || !_lateness.Written(r)) && shapes.All(s => !s.Contains(r)));

        /// <summary>
        /// Components some write of which on an entity that already exists may run after an await
        /// or a yield (a view's registrars: HueAnimator, once the view's asset is loaded): <see
        /// cref="GraphNode.AddedLate"/> when every way to every such write is late, <see
        /// cref="GraphNode.SometimesLate"/> when some way is not. An await wins over a yield. One
        /// way a system or a handler reaches without one, and it is no longer "only" late:
        /// WorldPosition, which some views also set once loaded, is mostly a system's. A reader
        /// whose condition asks for one (`if (target.hasHueAnimator)`) waits for it with no frame
        /// to tell.
        /// </summary>
        public static void MarkAddedLate(Lateness lateness, List<Access> accesses, List<GraphNode> nodes)
        {
            var late = new Dictionary<string, int>(StringComparer.Ordinal);
            var atOnce = new HashSet<string>(StringComparer.Ordinal);
            foreach (var write in accesses.Where(a => a.Relation == Relation.Creates && a.Origin.Created == null))
            {
                var ways = lateness.Of(write);
                late.TryGetValue(write.Component.Name, out var had);
                late[write.Component.Name] = had | (int)(ways & Ways.Late);
                if ((ways & Ways.Sync) != 0)
                    atOnce.Add(write.Component.Name);
            }
            foreach (var node in nodes)
            {
                if (node.Kind != NodeKind.Component || !late.TryGetValue(node.Label, out var kinds) || kinds == 0)
                    continue;
                var how = ((Ways)kinds & Ways.Await) != 0 ? AfterAwait : AfterYield;
                if (atOnce.Contains(node.Label))
                    node.SometimesLate = how;
                else
                    node.AddedLate = how;
            }
        }
    }

    /// <summary>The kinds of way to a piece of code: none suspended on the way, past a yield, past an await.</summary>
    [Flags]
    enum Ways : byte
    {
        None = 0,
        Sync = 1,
        Yield = 2,
        Await = 4,
        Late = Yield | Await
    }

    /// <summary>
    /// Whether code runs only after an await or a yield, per way to it rather than per method.
    /// EntityBehaviour.SetEntity is called after `await LoadAsync` when EntityViewFactory loads a
    /// view's asset, and at once when a view placed in the scene sets up its own entity
    /// (ProcessSelfInitializeRequestsSystem): what SetEntity adds (View, and its registrars'
    /// HueAnimator) is late on the first ways and not on the others. Marking a method late once
    /// some call to it was, at any depth, made everything such a factory reaches late everywhere.
    ///
    /// Ways start where the call graph sees no caller — a system's Execute, a Unity message, a
    /// handler wired in the inspector — and at methods called from outside a method (a field's
    /// initializer); a way is late from the first call on it made after an await or a yield
    /// (<see cref="CallSite.Late"/>), and an await wins over a yield. A method on late ways only
    /// that also has callers the graph cannot see — handed out as a delegate, a Unity message, a
    /// DLL's override or interface — starts a way of its own. A call passing the entity of a loop
    /// over a group requiring a component nothing in the project adds never runs, and no way goes
    /// through it: BindEntityViewFromPrefabSystem on a project that never sets ViewPrefab.
    ///
    /// Two answers: for the whole project, every way, any depth (<see cref="Of(Access)"/>: is a
    /// write only late?); and per system, as far as <see cref="Creations.Reach"/> goes, with the
    /// group of the loop its own call starts the way in (<see cref="From"/>: which of its ways to
    /// a write are late, for which entities?).
    /// </summary>
    sealed class Lateness
    {
        static readonly Ways[] Kinds = { Ways.Sync, Ways.Yield, Ways.Await };

        readonly BuildContext _context;
        readonly Creations _creations;
        readonly HashSet<string> _written;

        // Ways are kept as ints: collections of a byte enum would be generic code of their own to
        // compile, in a process that runs once.
        readonly Dictionary<IMethodSymbol, int> _ways = new Dictionary<IMethodSymbol, int>(SymbolEqualityComparer.Default);
        readonly Dictionary<IMethodSymbol, List<CallSite>> _live = new Dictionary<IMethodSymbol, List<CallSite>>(SymbolEqualityComparer.Default);
        readonly Dictionary<ISymbol, bool> _dead = new Dictionary<ISymbol, bool>(SymbolEqualityComparer.Default);
        readonly Dictionary<string, Dictionary<IMethodSymbol, List<Path>>> _bySystem = new Dictionary<string, Dictionary<IMethodSymbol, List<Path>>>(StringComparer.Ordinal);

        public Lateness(BuildContext context, Creations creations, List<Access> accesses, List<CallSite> calls, IEnumerable<IMethodSymbol> calledOutsideMethods)
        {
            _context = context;
            _creations = creations;
            var writes = accesses.Where(a => a.Relation == Relation.Creates).ToList();
            _written = new HashSet<string>(writes.Select(a => a.Component.Name), StringComparer.Ordinal);

            // No call made after a suspension: every way to every method is one without (but a
            // write past an await in its own method, which Of(Access) still tells).
            if (!calls.Any(c => c.Late != null))
                return;

            // Every method a call can land in, dead calls included: one only a loop that never
            // runs calls starts no way. The calls that can run, by caller.
            var called = new HashSet<IMethodSymbol>(SymbolEqualityComparer.Default);
            foreach (var call in calls)
            {
                var targets = _creations.Targets(call.Callee);
                called.UnionWith(targets);
                if (targets.Count == 0 || Dead(call))
                    continue;
                if (!_live.TryGetValue(call.Caller, out var live))
                    _live[call.Caller] = live = new List<CallSite>();
                live.Add(call);
            }
            var starts = _creations.Callees.Keys
                .Concat(writes.Where(a => a.Method != null).Select(a => a.Method!))
                .Where(m => !called.Contains(m))
                .Concat(calledOutsideMethods.SelectMany(_creations.Targets));
            Propagate(starts);

            // The methods on late ways only to a write on an existing entity, back from it.
            var writers = new HashSet<IMethodSymbol>(writes.Where(a => a.Origin.Created == null && a.Method != null && OnlyLate(a.Method)).Select(a => a.Method!),
                SymbolEqualityComparer.Default);
            if (writers.Count == 0)
                return;
            var callers = new Dictionary<IMethodSymbol, List<IMethodSymbol>>(SymbolEqualityComparer.Default);
            foreach (var pair in _live)
            {
                if (!OnlyLate(pair.Key))
                    continue;
                foreach (var site in pair.Value)
                foreach (var target in _creations.Targets(site.Callee))
                {
                    if (!OnlyLate(target))
                        continue;
                    if (!callers.TryGetValue(target, out var list))
                        callers[target] = list = new List<IMethodSymbol>();
                    list.Add(pair.Key);
                }
            }
            var queue = new Queue<IMethodSymbol>(writers);
            while (queue.Count > 0)
                if (callers.TryGetValue(queue.Dequeue(), out var up))
                    foreach (var caller in up)
                        if (writers.Add(caller))
                            queue.Enqueue(caller);

            var handedOut = Creations.HandedOut(context, writers.Select(m => m.Name));
            Propagate(writers.Where(m => handedOut.Contains(m.Name) || UnityMessage(m) || FromOutside(m)).OrderBy(m => m.ToDisplayString(), StringComparer.Ordinal));
        }

        /// <summary>A way a system reaches a method by: its kind, and the group whose entity the system's own call starting it passes.</summary>
        public sealed class Path
        {
            public readonly IMethodSymbol Method;
            public readonly Ways Kind;
            public readonly ISymbol? Entry;

            public Path(IMethodSymbol method, Ways kind, ISymbol? entry)
            {
                Method = method;
                Kind = kind;
                Entry = entry;
            }
        }

        /// <summary>Whether some write in the project adds the component, on an entity being made or not.</summary>
        public bool Written(string component) => _written.Contains(component);

        /// <summary>The kinds of way to the method, over the whole project; a method no way reaches (a cycle nothing calls) as reached at once.</summary>
        public Ways Of(IMethodSymbol? method) =>
            method != null && _ways.TryGetValue(method, out var ways) && ways != 0 ? (Ways)ways : Ways.Sync;

        /// <summary>The kinds of way to the write: its method's, all late when the write itself comes after a suspension there.</summary>
        public Ways Of(Access write) => Through(Of(write.Method), write.Late);

        /// <summary>The kinds of way past a call made at <paramref name="late"/> ("an await", "a yield", null).</summary>
        public static Ways Through(Ways ways, string? late) =>
            late == null || ways == Ways.None ? ways
            : late == "an await" ? Ways.Await
            : (ways & Ways.Await) | ((ways & (Ways.Sync | Ways.Yield)) != 0 ? Ways.Yield : Ways.None);

        /// <summary>
        /// The methods <paramref name="system"/> reaches through calls, as far as
        /// <see cref="Creations.Reach"/> goes, each with every way it is reached by: its kind, and
        /// the group whose entity the system's own call starting it passes (null when it passes
        /// none, several, or one not of a loop). Its own methods start as the project has them.
        /// </summary>
        public Dictionary<IMethodSymbol, List<Path>> From(TypeInfo system)
        {
            if (_bySystem.TryGetValue(system.Id, out var known))
                return known;
            var found = new Dictionary<IMethodSymbol, List<Path>>(SymbolEqualityComparer.Default);
            _bySystem[system.Id] = found;
            if (!_creations.MethodsOf.TryGetValue(system.Symbol, out var roots))
                return found;

            var frontier = new List<Path>();
            foreach (var root in roots)
            {
                var ways = Of(root);
                foreach (var kind in Kinds)
                    if ((ways & kind) != 0)
                        foreach (var site in _creations.Callees[root])
                            Step(frontier, site, kind, site.Group);
            }

            for (var depth = 0; depth < _context.Options.MaxCallDepth && frontier.Count > 0; depth++)
            {
                var next = new List<Path>();
                foreach (var path in frontier)
                {
                    if (_context.TypeOf(path.Method.ContainingType) is not { Kind: NodeKind.Type })
                        continue;
                    if (!found.TryGetValue(path.Method, out var paths))
                        found[path.Method] = paths = new List<Path>();
                    else if (paths.Any(p => p.Kind == path.Kind && SymbolEqualityComparer.Default.Equals(p.Entry, path.Entry)))
                        continue;
                    paths.Add(path);
                    if (_creations.Callees.TryGetValue(path.Method, out var sites))
                        foreach (var site in sites)
                            Step(next, site, path.Kind, path.Entry);
                }
                frontier = next;
            }
            return found;

            void Step(List<Path> into, CallSite site, Ways kind, ISymbol? entry)
            {
                if (Dead(site))
                    return;
                var past = Through(kind, site.Late);
                foreach (var target in _creations.Targets(site.Callee))
                    into.Add(new Path(target, past, entry));
            }
        }

        bool OnlyLate(IMethodSymbol method) => _ways.TryGetValue(method, out var ways) && ways != 0 && ((Ways)ways & Ways.Sync) == 0;

        /// <summary>Spreads ways from <paramref name="starts"/>, each reached at once, over every call that can run, at any depth.</summary>
        void Propagate(IEnumerable<IMethodSymbol> starts)
        {
            var queue = new Queue<IMethodSymbol>();
            foreach (var start in starts)
                if (Add(start, Ways.Sync))
                    queue.Enqueue(start);
            while (queue.Count > 0)
            {
                var method = queue.Dequeue();
                if (!_live.TryGetValue(method, out var sites))
                    continue;
                var ways = (Ways)_ways[method];
                foreach (var site in sites)
                {
                    var past = Through(ways, site.Late);
                    foreach (var target in _creations.Targets(site.Callee))
                        if (Add(target, past))
                            queue.Enqueue(target);
                }
            }

            bool Add(IMethodSymbol method, Ways ways)
            {
                _ways.TryGetValue(method, out var had);
                if ((had | (int)ways) == had)
                    return false;
                _ways[method] = had | (int)ways;
                return true;
            }
        }

        /// <summary>A call passing the entity of a loop over a group that requires a component nothing adds: the loop never runs.</summary>
        bool Dead(CallSite call)
        {
            if (call.Group == null)
                return false;
            if (!_dead.TryGetValue(call.Group, out var dead))
                _dead[call.Group] = dead = _creations.Required(call.Group).Any(c => !_written.Contains(c));
            return dead;
        }

        /// <summary>Unity calls a MonoBehaviour's Update, OnEnable or OnTriggerEnter by name; none of those calls is in the code.</summary>
        static bool UnityMessage(IMethodSymbol method)
        {
            if (!(method.Name is "Awake" or "Start" or "Update" or "FixedUpdate" or "LateUpdate" or "Reset" || method.Name.StartsWith("On", StringComparison.Ordinal)))
                return false;
            for (var type = method.ContainingType?.BaseType; type != null; type = type.BaseType)
                if (type.Name == "Object" && type.ContainingNamespace?.ToDisplayString() == "UnityEngine")
                    return true;
            return false;
        }

        /// <summary>Whether code outside the project may call the method: it overrides a DLL's method or implements a DLL's interface.</summary>
        static bool FromOutside(IMethodSymbol method)
        {
            for (var overridden = method.OverriddenMethod; overridden != null; overridden = overridden.OverriddenMethod)
                if (!overridden.Locations.Any(l => l.IsInSource))
                    return true;
            var type = method.ContainingType;
            return type != null && type.AllInterfaces.Where(i => !i.Locations.Any(l => l.IsInSource))
                .SelectMany(i => i.GetMembers())
                .Any(member => SymbolEqualityComparer.Default.Equals(type.FindImplementationForInterfaceMember(member), method));
        }
    }
}
