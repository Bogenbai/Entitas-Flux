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
    /// How it arrives is every write of it on an existing entity, but those whose loop runs over
    /// a group the entity can never match: after an await or a yield anywhere on the way to one
    /// of them, or in a type no system reaches, and no frame can be told
    /// (<see cref="ReaderWait.Added"/>); otherwise the systems making them, one of which the
    /// reader waits for (<see cref="ReaderWait.AddedBy"/>).
    /// </summary>
    sealed class Waits
    {
        const string AfterAwait = "after an await", AfterYield = "after a yield", Outside = "outside the systems";

        readonly BuildContext _context;
        readonly Creations _creations;
        readonly Dictionary<string, List<Access>> _setters;
        readonly HashSet<IMethodSymbol> _afterAwait;
        readonly HashSet<IMethodSymbol> _afterYield;
        readonly Dictionary<IMethodSymbol, SortedSet<string>> _runners = new Dictionary<IMethodSymbol, SortedSet<string>>(SymbolEqualityComparer.Default);
        readonly Dictionary<string, HashSet<IMethodSymbol>> _reach = new Dictionary<string, HashSet<IMethodSymbol>>(StringComparer.Ordinal);
        readonly Dictionary<string, (string? Added, List<string> By)?> _arrivals = new Dictionary<string, (string?, List<string>)?>(StringComparer.Ordinal);

        Waits(BuildContext context, Creations creations, List<Access> accesses, List<CallSite> calls)
        {
            _context = context;
            _creations = creations;
            _setters = accesses.Where(a => a.Relation == Relation.Creates && a.Origin.Created == null)
                .GroupBy(a => a.Component.Name)
                .ToDictionary(g => g.Key, g => g.ToList(), StringComparer.Ordinal);
            _afterAwait = RunsAfter(context, creations, calls, "an await");
            _afterYield = RunsAfter(context, creations, calls, "a yield");

            foreach (var system in context.Types.Values.Where(t => t.Kind == NodeKind.System))
            foreach (var method in creations.Reach(system))
            {
                if (!_runners.TryGetValue(method, out var runners))
                    _runners[method] = runners = new SortedSet<string>(StringComparer.Ordinal);
                runners.Add(system.Id);
            }
        }

        public static void Mark(BuildContext context, Creations creations, List<Access> accesses, List<CallSite> calls, HashSet<string> handOffs,
            Dictionary<string, GraphEdge> edges)
        {
            var writes = accesses.Where(a => a.Relation == Relation.Creates && handOffs.Contains(a.Component.Name)).ToList();
            var readers = Readers(accesses, handOffs, edges);
            if (writes.Count == 0 || readers.Count == 0)
                return;

            var waits = new Waits(context, creations, accesses, calls);
            var byAccessor = writes.GroupBy(a => a.Accessor.Id).ToDictionary(g => g.Key, g => g.ToList(), StringComparer.Ordinal);
            var byMethod = writes.Where(a => a.Method != null && a.Accessor.Kind == NodeKind.Type)
                .GroupBy<Access, IMethodSymbol>(a => a.Method!, SymbolEqualityComparer.Default)
                .ToDictionary<IGrouping<IMethodSymbol, Access>, IMethodSymbol, List<Access>>(g => g.Key, g => g.ToList(), SymbolEqualityComparer.Default);

            // Per edge, the creations its writes land on (null once one of them is not an entity
            // made in the same run), the helpers that was followed through, and who writes.
            var landsOn = new Dictionary<string, SortedSet<string>?>(StringComparer.Ordinal);
            var through = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
            var written = new Dictionary<string, (string Component, TypeInfo Writer)>(StringComparer.Ordinal);
            foreach (var system in context.Types.Values.Where(t => t.Kind == NodeKind.System))
            {
                var reached = creations.Reach(system);
                var own = byAccessor.TryGetValue(system.Id, out var found) ? found : new List<Access>();
                if (own.Count == 0 && !reached.Any(byMethod.ContainsKey))
                    continue;

                var inReach = new HashSet<IMethodSymbol>(reached, SymbolEqualityComparer.Default);
                bool InTree(IMethodSymbol method) =>
                    inReach.Contains(method) || SymbolEqualityComparer.Default.Equals(method.ContainingType?.OriginalDefinition, system.Symbol);

                var sites = own.Select(a => (Key: a.ToEdge(Confidence.Extracted, null).Key, Write: a))
                    .Concat(reached.Where(byMethod.ContainsKey).SelectMany(m => byMethod[m].Select(a =>
                        (Key: a.ToEdge(Confidence.Inferred, $"{m.ContainingType.Name}.{m.Name}", system.Id).Key, Write: a))));
                foreach (var (key, write) in sites)
                {
                    written[key] = (write.Component.Name, system);
                    if (landsOn.TryGetValue(key, out var keys) && keys == null)
                        continue;
                    var visited = new HashSet<(IMethodSymbol Method, int Parameter)>();
                    var resolved = creations.Resolve(write.Origin, InTree, visited, fresh: true);
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
                if (_creations.CreationOnly(needs) || Arrival(needs, keys, shapes) is not { } arrival)
                    return null;
                var wait = new ReaderWait { System = reader.Id, Needs = "component:" + needs, Added = arrival.Added };
                wait.AddedBy.AddRange(arrival.By);
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

        /// <summary>
        /// How the component arrives on an entity made at <paramref name="keys"/>: every write of
        /// it on an existing entity, but those in a loop over a group requiring something only
        /// creations add and the entity is not made with. "after an await", "after a yield" or
        /// "outside the systems" when one of them may be that; otherwise the systems making them.
        /// Null when there is none.
        /// </summary>
        (string? Added, List<string> By)? Arrival(string component, SortedSet<string> keys, List<HashSet<string>> shapes)
        {
            var id = component + "\u0001" + string.Join("\u0001", keys);
            if (_arrivals.TryGetValue(id, out var known))
                return known;

            (string? Added, List<string> By)? arrival = null;
            if (_setters.TryGetValue(component, out var setters))
            {
                var by = new SortedSet<string>(StringComparer.Ordinal);
                bool awaited = false, yielded = false, outside = false, any = false;
                foreach (var setter in setters)
                {
                    if (setter.Group != null && _creations.Required(setter.Group)
                            .Any(r => r != component && _creations.CreationOnly(r) && shapes.All(s => !s.Contains(r))))
                        continue;

                    any = true;
                    awaited |= setter.Late == "an await" || setter.Method != null && _afterAwait.Contains(setter.Method);
                    yielded |= setter.Late == "a yield" || setter.Method != null && _afterYield.Contains(setter.Method);
                    if (setter.Accessor.Kind == NodeKind.System)
                        by.Add(setter.Accessor.Id);
                    else if (setter.Accessor.Kind == NodeKind.Type && setter.Method != null && _runners.TryGetValue(setter.Method, out var runners))
                        by.UnionWith(runners);
                    else
                        outside = true;
                }

                if (any)
                {
                    var added = awaited ? AfterAwait : yielded ? AfterYield : outside ? Outside : null;
                    arrival = (added, added == null ? by.ToList() : new List<string>());
                }
            }

            _arrivals[id] = arrival;
            return arrival;
        }

        /// <summary>
        /// Components every write of which on an entity that already exists may run only after
        /// an await or a yield — in its own method past one, or in a method called past one, at
        /// any depth (a view's registrars: HueAnimator). An await wins over a yield. One write
        /// a system makes the ordinary way and it is no such component: WorldPosition, which
        /// some views also set once loaded, is mostly a system's. A reader whose condition asks
        /// for one (`if (target.hasHueAnimator)`) waits for it with no frame to tell.
        /// </summary>
        public static Dictionary<string, string> AddedLate(BuildContext context, Creations creations, List<Access> accesses, List<CallSite> calls)
        {
            var afterAwait = RunsAfter(context, creations, calls, "an await");
            var afterYield = RunsAfter(context, creations, calls, "a yield");
            var late = new Dictionary<string, string>(StringComparer.Ordinal);
            var onTime = new HashSet<string>(StringComparer.Ordinal);
            foreach (var write in accesses.Where(a => a.Relation == Relation.Creates && a.Origin.Created == null))
            {
                var name = write.Component.Name;
                var how = write.Late == "an await" || write.Method != null && afterAwait.Contains(write.Method) ? AfterAwait
                    : write.Late == "a yield" || write.Method != null && afterYield.Contains(write.Method) ? AfterYield
                    : null;
                if (how == null)
                    onTime.Add(name);
                else if (!late.TryGetValue(name, out var had) || had != AfterAwait)
                    late[name] = how;
            }
            foreach (var name in onTime)
                late.Remove(name);
            return late;
        }

        /// <summary>
        /// The methods that may run only after a method was suspended at <paramref name="late"/>
        /// ("an await", "a yield"): those called after one, and everything they call, however deep,
        /// through interfaces and abstract classes to every override (a view's registrars).
        /// </summary>
        static HashSet<IMethodSymbol> RunsAfter(BuildContext context, Creations creations, List<CallSite> calls, string late)
        {
            var found = new HashSet<IMethodSymbol>(SymbolEqualityComparer.Default);
            var queue = new Queue<IMethodSymbol>();
            foreach (var call in calls.Where(c => c.Late == late))
            foreach (var method in context.DispatchAll(new[] { call.Callee }))
                if (found.Add(method))
                    queue.Enqueue(method);

            while (queue.Count > 0)
            {
                if (!creations.Callees.TryGetValue(queue.Dequeue(), out var further))
                    continue;
                foreach (var call in further)
                foreach (var method in context.DispatchAll(new[] { call.Callee }))
                    if (found.Add(method))
                        queue.Enqueue(method);
            }
            return found;
        }
    }
}
