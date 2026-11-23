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
    /// What is known for certain about the entities the project makes, for the conclusions that
    /// need it (<see cref="UnseenReactors"/>, <see cref="Waits"/>): every creation and the factory
    /// calls handing its entity back, the components each one is made with, the components only
    /// creations add, what a group's AllOf requires, and the methods a system reaches through
    /// calls. Built once per graph, and only when no write in the project escapes the generated
    /// API (AddComponent with a computed index, CopyTo): otherwise no shape is known.
    /// </summary>
    sealed class Creations
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
        readonly Dictionary<string, List<IMethodSymbol>> _reach = new Dictionary<string, List<IMethodSymbol>>(StringComparer.Ordinal);

        /// <summary>The project's call sites by the method making them.</summary>
        public readonly Dictionary<IMethodSymbol, List<CallSite>> Callees;

        /// <summary>The methods making calls, by the type declaring them.</summary>
        public readonly Dictionary<ISymbol, List<IMethodSymbol>> MethodsOf;

        public Creations(BuildContext context, List<Access> accesses, List<CallSite> calls, List<ArgumentSite> arguments)
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

            Callees = calls.GroupBy<CallSite, IMethodSymbol>(c => c.Caller, SymbolEqualityComparer.Default)
                .ToDictionary<IGrouping<IMethodSymbol, CallSite>, IMethodSymbol, List<CallSite>>(g => g.Key, g => g.ToList(), SymbolEqualityComparer.Default);
            MethodsOf = Callees.Keys.GroupBy<IMethodSymbol, ISymbol>(m => m.ContainingType.OriginalDefinition, SymbolEqualityComparer.Default)
                .ToDictionary<IGrouping<ISymbol, IMethodSymbol>, ISymbol, List<IMethodSymbol>>(g => g.Key, g => g.ToList(), SymbolEqualityComparer.Default);
        }

        /// <summary>Components every write of which is on an entity being made right there: none can arrive on an existing entity.</summary>
        public bool CreationOnly(string component) => _creationOnly.Contains(component);

        EntityMethod? Facts(IMethodSymbol method) => _context.EntityMethods.Of(_context, method, new OriginTrace());

        /// <summary>
        /// Of these method names, those used anywhere other than as what a call invokes: a method
        /// group (`entities.ForEach(Drain)`, `OnAdded += Pay`), nameof. A method handed out runs
        /// with whatever the delegate is given, which no call site shows. By name, so a field of
        /// the same name counts too — it only costs an exclusion. Only trees whose text has one
        /// of the names are read token by token, in parallel.
        /// </summary>
        public static HashSet<string> HandedOut(BuildContext context, IEnumerable<string> names)
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
        /// A group's AllOf as written: what every entity of it carries. Not the markers it reacts
        /// to — an entity of the group carries XChanged, not necessarily X — and a lone matcher
        /// only when it is the group's only one (GetGroup(GameMatcher.X)), since a matcher in an
        /// array passed to AnyOf reads the same.
        /// </summary>
        public List<string> Required(ISymbol group)
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

        /// <summary>
        /// The methods a system reaches through calls, as Propagate walks them: what it writes
        /// there is credited to it. Without Propagate's pruning by constant arguments, which only
        /// makes it reach more. Worked out once per system.
        /// </summary>
        public List<IMethodSymbol> Reach(TypeInfo system)
        {
            if (_reach.TryGetValue(system.Id, out var known))
                return known;

            var reached = new List<IMethodSymbol>();
            _reach[system.Id] = reached;
            if (!MethodsOf.TryGetValue(system.Symbol, out var roots))
                return reached;

            var visited = new HashSet<IMethodSymbol>(SymbolEqualityComparer.Default);
            var frontier = roots.SelectMany(r => Callees[r]).SelectMany(c => _context.Dispatch(new[] { c.Callee })).ToList();
            for (var depth = 0; depth < _context.Options.MaxCallDepth && frontier.Count > 0; depth++)
            {
                var next = new List<IMethodSymbol>();
                foreach (var method in frontier)
                {
                    if (!visited.Add(method) || _context.TypeOf(method.ContainingType) is not { Kind: NodeKind.Type })
                        continue;
                    reached.Add(method);
                    if (Callees.TryGetValue(method, out var further))
                        next.AddRange(further.SelectMany(c => _context.Dispatch(new[] { c.Callee })));
                }
                frontier = next;
            }
            return reached;
        }

        /// <summary>
        /// The creations the entity can come from, by their keys; null when that is not known. A
        /// parameter is followed to what the callers in the writing system's reach pass (none of
        /// them: unknown); a call cycle adds nothing of its own. With <paramref name="fresh"/>, only
        /// an entity made in the same run counts: one of a group's, or one an index finds, may have
        /// got anything since it was made, so it is unknown.
        /// </summary>
        public List<string>? Resolve(EntityOrigin origin, Func<IMethodSymbol, bool> inTree, HashSet<(IMethodSymbol, int)> visiting, bool fresh = false)
        {
            if (origin.Created != null)
                return Up(origin.Created);
            if (origin.Group != null)
                return fresh ? null : Carrying(Required(origin.Group));
            if (origin.Carries != null)
                return fresh ? null : Carrying(new[] { origin.Carries });
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
                var found = Resolve(site.Origin, inTree, visiting, fresh);
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
        public HashSet<string>? Shape(string key)
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
    }
}
