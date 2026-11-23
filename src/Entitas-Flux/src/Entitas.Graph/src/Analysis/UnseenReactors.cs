using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Entitas.Graph.Analysis
{
    /// <summary>
    /// The systems reacting to a [Watched] component's XChanged marker that can never see a given
    /// write of it, because their group requires a component the written entity never gets.
    /// CatsAnimationSystem reacts to MovingChanged on AllOf(Cat, CatAnimator); a furniture factory
    /// clearing Moving on the furniture it builds sets the marker, and a flow listed the cat
    /// animation as taking it. Groups and creation sites exist only while the graph is built, so
    /// the answer goes into graph.json, on the write (<see cref="GraphEdge.UnseenBy"/>).
    ///
    /// It must be sound, since a reactor left out of a flow is not looked for again. A reactor is
    /// left out only when, for every entity the write can land on and every group of the reactor
    /// taking the marker, the group's AllOf has a component the entity never gets:
    /// <list type="bullet">
    /// <item>The entity is known: made right there or by a factory (its components at creation
    /// are every write on it there, and what the factories it came through put on it), passed to a
    /// helper by a caller whose entity is known — callers in the writing system's own reach only,
    /// the methods Propagate credits to it — one of a group's entities, or the one a primary index
    /// finds. Of those it is known that they were made where everything the group requires (the
    /// indexed component) that only entities being made get was put on them, so every such
    /// creation is a candidate.</item>
    /// <item>The component is absent from the entity's creation and is added nowhere but where
    /// entities are made: a write on an existing entity, a helper's parameter or anything
    /// untraced, and it can arrive later — the animator a view adds, ProductStandAnimator — so
    /// the reactor stays.</item>
    /// <item>A helper's parameter is followed only when nothing else can call the helper: not
    /// one handed out as a delegate, an override or a DLL interface's implementation. A type that
    /// is no system is called by whoever: what its own methods are handed is not known.</item>
    /// <item>A project adding components the generated API does not name (AddComponent with a
    /// computed index, CopyTo) gets no exclusions at all.</item>
    /// </list>
    /// What the group requires is its AllOf as written, without the markers it reacts to; a
    /// reactor with a reacting group that is not traced to a field or local (a collector built
    /// in a return, a marker read on an entity, a query) stays.
    /// </summary>
    sealed class UnseenReactors
    {
        readonly BuildContext _context;
        readonly Dictionary<ISymbol, List<Access>> _matchers;
        readonly HashSet<string> _creationOnly;
        readonly Dictionary<string, HashSet<string>> _written = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        readonly Dictionary<string, EntityOrigin> _creations = new Dictionary<string, EntityOrigin>(StringComparer.Ordinal);
        readonly Dictionary<string, List<string>> _outer = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        readonly Dictionary<string, HashSet<string>?> _shapes = new Dictionary<string, HashSet<string>?>(StringComparer.Ordinal);
        readonly Dictionary<string, List<string>?> _carrying = new Dictionary<string, List<string>?>(StringComparer.Ordinal);
        readonly Dictionary<ISymbol, List<string>> _required = new Dictionary<ISymbol, List<string>>(SymbolEqualityComparer.Default);
        readonly Dictionary<IMethodSymbol, List<ArgumentSite>> _passed = new Dictionary<IMethodSymbol, List<ArgumentSite>>(SymbolEqualityComparer.Default);

        UnseenReactors(BuildContext context, List<Access> accesses, List<ArgumentSite> arguments)
        {
            _context = context;
            _matchers = accesses.Where(a => a.MatcherGroup != null)
                .GroupBy(a => a.MatcherGroup!, SymbolEqualityComparer.Default)
                .ToDictionary(g => g.Key, g => g.ToList(), SymbolEqualityComparer.Default);

            var creates = accesses.Where(a => a.Relation == Relation.Creates).ToList();
            _creationOnly = new HashSet<string>(creates.GroupBy(a => a.Component.Name).Where(g => g.All(a => a.Origin.Created != null)).Select(g => g.Key),
                StringComparer.Ordinal);
            foreach (var create in creates.Where(a => a.Origin.Created != null))
            {
                if (!_written.TryGetValue(create.Origin.Created!, out var names))
                    _written[create.Origin.Created!] = names = new HashSet<string>(StringComparer.Ordinal);
                names.Add(create.Component.Name);
            }

            // Every creation seen, and the factory calls each new entity is handed back through.
            var queue = new Queue<EntityOrigin>(accesses.Select(a => a.Origin).Concat(arguments.Select(a => a.Origin)).Where(o => o.Created != null));
            while (queue.Count > 0)
            {
                var origin = queue.Dequeue();
                if (_creations.ContainsKey(origin.Created!))
                    continue;
                _creations[origin.Created!] = origin;
                if (origin.Factory == null || Facts(origin.Factory)?.Creates is not { } inner)
                    continue;
                foreach (var made in inner)
                {
                    if (!_outer.TryGetValue(made.Created!, out var outer))
                        _outer[made.Created!] = outer = new List<string>();
                    outer.Add(origin.Created!);
                    queue.Enqueue(made);
                }
            }

            foreach (var site in arguments)
            foreach (var method in context.Dispatch(new[] { site.Callee }))
            {
                if (!_passed.TryGetValue(method, out var sites))
                    _passed[method] = sites = new List<ArgumentSite>();
                sites.Add(site);
            }
        }

        public static void Mark(BuildContext context, List<Access> accesses, List<CallSite> calls, List<ArgumentSite> arguments, bool untracedWrites,
            Dictionary<string, GraphEdge> edges)
        {
            if (untracedWrites)
                return;

            var unseen = new UnseenReactors(context, accesses, arguments);
            var reactors = unseen.Reactors(accesses, edges);
            if (reactors.Count == 0)
                return;

            var writes = accesses.Where(a => a.Relation is Relation.Creates or Relation.Removes && reactors.ContainsKey(a.Component.Name)).ToList();
            var byAccessor = writes.GroupBy(a => a.Accessor.Id).ToDictionary(g => g.Key, g => g.ToList(), StringComparer.Ordinal);
            var byMethod = writes.Where(a => a.Method != null && a.Accessor.Kind == NodeKind.Type)
                .GroupBy<Access, IMethodSymbol>(a => a.Method!, SymbolEqualityComparer.Default)
                .ToDictionary<IGrouping<IMethodSymbol, Access>, IMethodSymbol, List<Access>>(g => g.Key, g => g.ToList(), SymbolEqualityComparer.Default);
            var callees = calls.GroupBy<CallSite, IMethodSymbol>(c => c.Caller, SymbolEqualityComparer.Default)
                .ToDictionary<IGrouping<IMethodSymbol, CallSite>, IMethodSymbol, List<CallSite>>(g => g.Key, g => g.ToList(), SymbolEqualityComparer.Default);
            var methodsOf = callees.Keys.GroupBy<IMethodSymbol, ISymbol>(m => m.ContainingType.OriginalDefinition, SymbolEqualityComparer.Default)
                .ToDictionary<IGrouping<ISymbol, IMethodSymbol>, ISymbol, List<IMethodSymbol>>(g => g.Key, g => g.ToList(), SymbolEqualityComparer.Default);

            // Per edge, every entity its writes can land on (null once one of them is not known),
            // and the helpers whose parameters that was followed through.
            var landsOn = new Dictionary<string, SortedSet<string>?>(StringComparer.Ordinal);
            var through = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
            var component = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var accessor in context.Types.Values)
            {
                var reached = accessor.Kind is NodeKind.System or NodeKind.Feature
                    ? unseen.Reach(accessor, callees, methodsOf)
                    : new List<IMethodSymbol>();
                var own = byAccessor.TryGetValue(accessor.Id, out var found) ? found : new List<Access>();
                if (own.Count == 0 && !reached.Any(byMethod.ContainsKey))
                    continue;

                // A system's write is followed back through the calls it makes itself. A type runs
                // when anybody calls it: what its methods are handed is not known.
                var inReach = new HashSet<IMethodSymbol>(reached, SymbolEqualityComparer.Default);
                var runs = accessor.Kind is NodeKind.System or NodeKind.Feature;
                bool InTree(IMethodSymbol method) =>
                    runs && (inReach.Contains(method) || SymbolEqualityComparer.Default.Equals(method.ContainingType?.OriginalDefinition, accessor.Symbol));

                var sites = own.Select(a => (Key: a.ToEdge(Confidence.Extracted, null).Key, Write: a))
                    .Concat(reached.Where(byMethod.ContainsKey).SelectMany(m => byMethod[m].Select(a =>
                        (Key: a.ToEdge(Confidence.Inferred, $"{m.ContainingType.Name}.{m.Name}", accessor.Id).Key, Write: a))));
                foreach (var (key, write) in sites)
                {
                    component[key] = write.Component.Name;
                    if (landsOn.TryGetValue(key, out var keys) && keys == null)
                        continue;
                    var visited = new HashSet<(IMethodSymbol Method, int Parameter)>();
                    var resolved = unseen.Resolve(write.Origin, InTree, visited);
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
                var name = component[pair.Key];
                foreach (var (system, groups) in reactors[name])
                {
                    var needs = unseen.Needs(name, groups, pair.Value);
                    if (needs != null)
                        edge.UnseenBy.Add(new UnseenReactor { System = system.Id, Needs = "component:" + needs });
                }
                edge.UnseenBy.Sort((a, b) => string.CompareOrdinal(a.System, b.System));
            }

            // A helper also handed out as a delegate runs on whatever the delegate is given, so
            // what its parameter was followed to says nothing: those edges keep every reactor.
            // Looked for last, and only for the helpers an exclusion depends on — reading every
            // tree's tokens for every helper cost a tenth of the build.
            var marked = edges.Values.Where(e => e.UnseenBy.Count > 0 && through.ContainsKey(e.Key)).ToList();
            var handedOut = HandedOut(context, marked.SelectMany(e => through[e.Key]));
            foreach (var edge in marked.Where(e => through[e.Key].Overlaps(handedOut)))
                edge.UnseenBy.Clear();
        }

        EntityMethod? Facts(IMethodSymbol method) => _context.EntityMethods.Of(_context, method, new OriginTrace());

        /// <summary>
        /// Of these method names, those used anywhere other than as what a call invokes: a method
        /// group (`entities.ForEach(Drain)`, `OnAdded += Pay`), nameof. A method handed out runs
        /// with whatever the delegate is given, which no call site shows. By name, so a field of
        /// the same name counts too — it only costs an exclusion. Only trees whose text has one
        /// of the names are read token by token, in parallel.
        /// </summary>
        static HashSet<string> HandedOut(BuildContext context, IEnumerable<string> names)
        {
            var asked = new HashSet<string>(names, StringComparer.Ordinal);
            var found = new ConcurrentDictionary<string, bool>(StringComparer.Ordinal);
            if (asked.Count == 0)
                return new HashSet<string>(StringComparer.Ordinal);

            Parallel.ForEach(context.Full.SyntaxTrees.Where(t => !context.IsGenerated(t)), tree =>
            {
                var text = tree.GetText().ToString();
                if (!asked.Any(n => text.IndexOf(n, StringComparison.Ordinal) >= 0))
                    return;
                foreach (var token in tree.GetRoot().DescendantTokens())
                {
                    if (!token.IsKind(SyntaxKind.IdentifierToken) || !asked.Contains(token.ValueText) ||
                        token.Parent is not SimpleNameSyntax name)
                        continue;
                    SyntaxNode target = name.Parent is MemberAccessExpressionSyntax access && access.Name == name ? access
                        : name.Parent is MemberBindingExpressionSyntax binding ? binding
                        : name;
                    if (!(target.Parent is InvocationExpressionSyntax call && call.Expression == target))
                        found[token.ValueText] = true;
                }
            });
            return new HashSet<string>(found.Keys, StringComparer.Ordinal);
        }

        /// <summary>
        /// A method code outside the project may call: an override, or an implementation of an
        /// interface from a DLL (a comparer List.Sort calls). Its parameters are whatever that code passes.
        /// </summary>
        static bool CalledBack(IMethodSymbol method)
        {
            if (method.IsOverride || method.IsVirtual || method.IsAbstract)
                return true;
            var type = method.ContainingType;
            return type != null && type.AllInterfaces.SelectMany(i => i.GetMembers()).Any(member =>
                member.Locations.All(l => !l.IsInSource) && SymbolEqualityComparer.Default.Equals(type.FindImplementationForInterfaceMember(member), method));
        }

        /// <summary>
        /// Per [Watched] component, the systems reacting to its marker whose every reaction is a
        /// group of their own with a known AllOf. A system reacting any other way — reading the
        /// marker on an entity, through a query, a collector built in a return — is never left out.
        /// </summary>
        Dictionary<string, List<(TypeInfo System, List<ISymbol> Groups)>> Reactors(List<Access> accesses, Dictionary<string, GraphEdge> edges)
        {
            var inferred = new HashSet<(string, string)>(edges.Values
                .Where(e => e.Relation == Relation.Reacts && e.Confidence == Confidence.Inferred)
                .Select(e => (e.Source, e.Target)));
            var reactors = new Dictionary<string, List<(TypeInfo, List<ISymbol>)>>(StringComparer.Ordinal);
            foreach (var reaction in accesses
                         .Where(a => a.Relation == Relation.Reacts && a.Accessor.Kind == NodeKind.System && !a.Accessor.Generated)
                         .GroupBy(a => (a.Component, a.Accessor)))
            {
                var (component, system) = reaction.Key;
                if (reaction.Any(a => a.MatcherGroup == null) || inferred.Contains((system.Id, component.Id)))
                    continue;
                var groups = reaction.Select(a => a.MatcherGroup!).Distinct(SymbolEqualityComparer.Default).ToList();
                if (groups.Any(g => Required(g).Count == 0))
                    continue;
                if (!reactors.TryGetValue(component.Name, out var list))
                    reactors[component.Name] = list = new List<(TypeInfo, List<ISymbol>)>();
                list.Add((system, groups));
            }
            return reactors;
        }

        /// <summary>
        /// A group's AllOf as written: what every entity of it carries. Not the markers it reacts
        /// to — an entity of the group carries XChanged, not necessarily X — and a lone matcher
        /// only when it is the group's only one (GetGroup(GameMatcher.X)), since a matcher in an
        /// array passed to AnyOf reads the same.
        /// </summary>
        List<string> Required(ISymbol group)
        {
            if (_required.TryGetValue(group, out var known))
                return known;
            var matchers = _matchers.TryGetValue(group, out var list) ? list : new List<Access>();
            var required = matchers
                .Where(a => a.Relation == Relation.Reads && (a.Clause == "AllOf" || a.Clause == "Matcher" && matchers.Count == 1))
                .Select(a => a.Component.Name)
                .Distinct()
                .ToList();
            _required[group] = required;
            return required;
        }

        /// <summary>The methods a system reaches through calls, as Propagate walks them: what it writes there is credited to it.</summary>
        List<IMethodSymbol> Reach(TypeInfo system, Dictionary<IMethodSymbol, List<CallSite>> callees, Dictionary<ISymbol, List<IMethodSymbol>> methodsOf)
        {
            var reached = new List<IMethodSymbol>();
            if (!methodsOf.TryGetValue(system.Symbol, out var roots))
                return reached;

            var visited = new HashSet<IMethodSymbol>(SymbolEqualityComparer.Default);
            var frontier = roots.SelectMany(r => callees[r]).SelectMany(c => _context.Dispatch(new[] { c.Callee })).ToList();
            for (var depth = 0; depth < _context.Options.MaxCallDepth && frontier.Count > 0; depth++)
            {
                var next = new List<IMethodSymbol>();
                foreach (var method in frontier)
                {
                    if (!visited.Add(method) || _context.TypeOf(method.ContainingType) is not { Kind: NodeKind.Type })
                        continue;
                    reached.Add(method);
                    if (callees.TryGetValue(method, out var further))
                        next.AddRange(further.SelectMany(c => _context.Dispatch(new[] { c.Callee })));
                }
                frontier = next;
            }
            return reached;
        }

        /// <summary>
        /// The creations the entity can come from, by their keys; null when that is not known. A
        /// parameter is followed to what the callers in the writing system's reach pass (none of
        /// them: unknown); a call cycle adds nothing of its own.
        /// </summary>
        List<string>? Resolve(EntityOrigin origin, Func<IMethodSymbol, bool> inTree, HashSet<(IMethodSymbol, int)> visiting)
        {
            if (origin.Created != null)
                return Up(origin.Created);
            if (origin.Group != null)
                return Carrying(Required(origin.Group));
            if (origin.Carries != null)
                return Carrying(new[] { origin.Carries });
            if (origin.Method == null || CalledBack(origin.Method))
                return null;
            if (!visiting.Add((origin.Method, origin.Parameter)))
                return new List<string>();

            var sites = _passed.TryGetValue(origin.Method, out var all)
                ? all.Where(s => s.Parameter == origin.Parameter && inTree(s.Caller)).ToList()
                : new List<ArgumentSite>();
            if (sites.Count == 0)
                return null;
            var keys = new List<string>();
            foreach (var site in sites)
            {
                var found = Resolve(site.Origin, inTree, visiting);
                if (found == null)
                    return null;
                keys.AddRange(found);
            }
            return keys;
        }

        /// <summary>A creation and every factory call handing its entity back, outward: the entity is whatever the outermost one made of it.</summary>
        List<string> Up(string key)
        {
            var keys = new List<string>();
            var seen = new HashSet<string>(StringComparer.Ordinal);
            var queue = new Queue<string>();
            queue.Enqueue(key);
            while (queue.Count > 0)
            {
                var current = queue.Dequeue();
                if (!seen.Add(current))
                    continue;
                keys.Add(current);
                if (_outer.TryGetValue(current, out var outer))
                    foreach (var up in outer)
                        queue.Enqueue(up);
            }
            return keys;
        }

        /// <summary>
        /// The creations an entity carrying <paramref name="components"/> (a group's AllOf, an
        /// index's component) can come from: every one whose components include those of them
        /// only creations add — it carried them, so it was made with them. None of those (state
        /// anything can get): unknown.
        /// </summary>
        List<string>? Carrying(IReadOnlyList<string> components)
        {
            var marks = components.Where(_creationOnly.Contains).OrderBy(c => c, StringComparer.Ordinal).ToList();
            var id = string.Join("\u0001", marks);
            if (_carrying.TryGetValue(id, out var known))
                return known;

            List<string>? keys = null;
            if (marks.Count > 0)
            {
                // A creation whose components are not known may carry anything: it stays a candidate.
                keys = _creations.Keys.Where(k => Shape(k) is not { } shape || marks.All(shape.Contains))
                    .OrderBy(k => k, StringComparer.Ordinal)
                    .ToList();
                if (keys.Count == 0)
                    keys = null;
            }
            _carrying[id] = keys;
            return keys;
        }

        /// <summary>
        /// The components an entity has when it is made at <paramref name="key"/>: every write on
        /// it there, and what the factory it came from put on its own new entity. Null when a
        /// factory cannot be read again or calls itself.
        /// </summary>
        HashSet<string>? Shape(string key)
        {
            if (_shapes.TryGetValue(key, out var known))
                return known;
            _shapes[key] = null;

            var shape = new HashSet<string>(_written.TryGetValue(key, out var written) ? written : Enumerable.Empty<string>(), StringComparer.Ordinal);
            if (_creations.TryGetValue(key, out var origin) && origin.Factory != null)
            {
                if (Facts(origin.Factory)?.Creates is not { } inner)
                    return null;
                foreach (var made in inner)
                {
                    if (Shape(made.Created!) is not { } part)
                        return null;
                    shape.UnionWith(part);
                }
            }

            _shapes[key] = shape;
            return shape;
        }

        /// <summary>
        /// A component every reacting group of the system requires and none of the entity's
        /// possible creations carries or can get later; null when the system may see the write.
        /// One that rules out all of them at once if there is one, else the first by name.
        /// </summary>
        string? Needs(string component, List<ISymbol> groups, SortedSet<string> keys)
        {
            HashSet<string>? common = null;
            string? first = null;
            foreach (var group in groups)
            foreach (var key in keys)
            {
                if (Shape(key) is not { } shape)
                    return null;
                var missing = Required(group).Where(c => c != component && _creationOnly.Contains(c) && !shape.Contains(c))
                    .OrderBy(c => c, StringComparer.Ordinal)
                    .ToList();
                if (missing.Count == 0)
                    return null;
                first ??= missing[0];
                if (common == null)
                    common = new HashSet<string>(missing, StringComparer.Ordinal);
                else
                    common.IntersectWith(missing);
            }
            return common is { Count: > 0 } ? common.OrderBy(c => c, StringComparer.Ordinal).First() : first;
        }
    }
}
