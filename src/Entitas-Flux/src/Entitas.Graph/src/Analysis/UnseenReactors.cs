using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.CodeAnalysis;

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
    /// computed index, CopyTo) gets no exclusions at all: <see cref="Creations"/> is not built.</item>
    /// </list>
    /// What the group requires is its AllOf as written, without the markers it reacts to; a
    /// reactor with a reacting group that is not traced to a field or local (a collector built
    /// in a return, a marker read on an entity, a query) stays.
    /// </summary>
    sealed class UnseenReactors
    {
        readonly Creations _creations;

        UnseenReactors(Creations creations) => _creations = creations;

        public static void Mark(BuildContext context, Creations creations, List<Access> accesses, Dictionary<string, GraphEdge> edges)
        {
            var unseen = new UnseenReactors(creations);
            var reactors = unseen.Reactors(accesses, edges);
            if (reactors.Count == 0)
                return;

            var writes = accesses.Where(a => a.Relation is Relation.Creates or Relation.Removes && reactors.ContainsKey(a.Component.Name)).ToList();
            var byAccessor = writes.GroupBy(a => a.Accessor.Id).ToDictionary(g => g.Key, g => g.ToList(), StringComparer.Ordinal);
            var byMethod = writes.Where(a => a.Method != null && a.Accessor.Kind == NodeKind.Type)
                .GroupBy<Access, IMethodSymbol>(a => a.Method!, SymbolEqualityComparer.Default)
                .ToDictionary<IGrouping<IMethodSymbol, Access>, IMethodSymbol, List<Access>>(g => g.Key, g => g.ToList(), SymbolEqualityComparer.Default);

            // Per edge, every entity its writes can land on (null once one of them is not known),
            // and the helpers whose parameters that was followed through.
            var landsOn = new Dictionary<string, SortedSet<string>?>(StringComparer.Ordinal);
            var through = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
            var component = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var accessor in context.Types.Values)
            {
                var reached = accessor.Kind is NodeKind.System or NodeKind.Feature
                    ? creations.Reach(accessor)
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
                    var resolved = creations.Resolve(write.Origin, InTree, visited);
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
            var handedOut = Creations.HandedOut(context, marked.SelectMany(e => through[e.Key]));
            foreach (var edge in marked.Where(e => through[e.Key].Overlaps(handedOut)))
                edge.UnseenBy.Clear();
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
                if (groups.Any(g => _creations.Required(g).Count == 0))
                    continue;
                if (!reactors.TryGetValue(component.Name, out var list))
                    reactors[component.Name] = list = new List<(TypeInfo, List<ISymbol>)>();
                list.Add((system, groups));
            }
            return reactors;
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
                if (_creations.Shape(key) is not { } shape)
                    return null;
                var missing = _creations.Required(group).Where(c => c != component && _creations.CreationOnly(c) && !shape.Contains(c))
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
