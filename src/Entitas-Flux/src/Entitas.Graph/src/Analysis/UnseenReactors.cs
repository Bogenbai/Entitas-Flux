using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.CodeAnalysis;
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
    /// computed index, CopyTo) gets no exclusions at all: <see cref="Creations"/> is not built.</item>
    /// </list>
    /// What the group requires is its AllOf as written, without the markers it reacts to; a
    /// reactor with a reacting group that is not traced to a field or local (a collector built
    /// in a return, a marker read on an entity, a query) stays.
    ///
    /// A reactor whose group needs what the write's own call takes off is only marked as one that
    /// may miss it (<see cref="GraphEdge.MayMiss"/>, <see cref="MarkMayMiss"/>): that is no
    /// certainty either.
    /// </summary>
    sealed class UnseenReactors
    {
        readonly BuildContext _context;
        readonly Creations _creations;
        readonly Dictionary<SyntaxTree, SemanticModel> _models = new Dictionary<SyntaxTree, SemanticModel>();

        UnseenReactors(BuildContext context, Creations creations)
        {
            _context = context;
            _creations = creations;
        }

        public static void Mark(BuildContext context, Creations creations, List<Access> accesses, Dictionary<string, GraphEdge> edges)
        {
            var unseen = new UnseenReactors(context, creations);
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
            var written = new List<(string Key, Access Write, Func<IMethodSymbol, bool>? InTree)>();
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
                    written.Add((key, write, runs ? InTree : null));
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

            unseen.MarkMayMiss(accesses, written, reactors, edges);
        }

        /// <summary>
        /// The reactors a write may miss because its own run takes off what they need (graph.json
        /// `may_miss`). Detaching a view removes View and, through the view's registrars, the
        /// CatAnimator a cat animation's group requires: after the call the cat cannot match it,
        /// and the marker is cleared at the end of the frame. Yet that it never reacts is not
        /// certain, so the reactor is not left out, only marked:
        /// <list type="bullet">
        /// <item>The registrar removes it from `Entity`, a property over a field another method
        /// set: that this is the entity written holds only by convention.</item>
        /// <item>Even on the same entity, the component may come back before the reactor runs —
        /// the next view attached puts CatAnimator on again — and the reactor then sees the
        /// marker this write set. Only a frame-by-frame order of every writer could rule it out.</item>
        /// </list>
        /// The run is the writing method and what it calls (an interface call reaching every
        /// implementation), within what Propagate credits to the system. A removal counts unless
        /// its entity is certainly another one: both known (<see cref="EntityOrigin"/>, a helper's
        /// parameter followed to the calls the run makes) and different — another parameter, a
        /// new entity, another group; or unless no call of the writing method runs both, the
        /// removal (or every call leading to it) and the write sitting in branches that exclude
        /// each other (<see cref="Exclusive"/>). A run that also adds the component marks nothing
        /// for it, and a reactor is marked only when every reacting group requires the component
        /// and every site of the edge says so.
        /// </summary>
        void MarkMayMiss(List<Access> accesses, List<(string Key, Access Write, Func<IMethodSymbol, bool>? InTree)> written,
            Dictionary<string, List<(TypeInfo System, List<ISymbol> Groups)>> reactors, Dictionary<string, GraphEdge> edges)
        {
            // What each reactor's groups all require, and per [Watched] component everything one of its reactors does.
            var required = reactors.ToDictionary(r => r.Key, r => r.Value.Select(x => (x.System, Needs: x.Groups
                    .Select(g => (IEnumerable<string>)_creations.Required(g))
                    .Aggregate((a, b) => a.Intersect(b, StringComparer.Ordinal))
                    .Where(c => c != r.Key)
                    .ToHashSet(StringComparer.Ordinal)))
                .Where(x => x.Needs.Count > 0)
                .ToList(), StringComparer.Ordinal);
            var wanted = new HashSet<string>(required.Values.SelectMany(l => l.SelectMany(x => x.Needs)), StringComparer.Ordinal);
            if (wanted.Count == 0)
                return;

            var removes = accesses.Where(a => a.Relation == Relation.Removes && a.Method != null && wanted.Contains(a.Component.Name))
                .GroupBy<Access, IMethodSymbol>(a => a.Method!, SymbolEqualityComparer.Default)
                .ToDictionary<IGrouping<IMethodSymbol, Access>, IMethodSymbol, List<Access>>(g => g.Key, g => g.ToList(), SymbolEqualityComparer.Default);
            var adds = accesses.Where(a => a.Relation == Relation.Creates && a.Method != null && wanted.Contains(a.Component.Name))
                .GroupBy<Access, IMethodSymbol>(a => a.Method!, SymbolEqualityComparer.Default)
                .ToDictionary<IGrouping<IMethodSymbol, Access>, IMethodSymbol, HashSet<string>>(
                    g => g.Key, g => new HashSet<string>(g.Select(a => a.Component.Name), StringComparer.Ordinal), SymbolEqualityComparer.Default);

            // Per edge, the reactors every site may hide it from, each with what it needs.
            var missed = new Dictionary<string, Dictionary<string, SortedSet<string>>>(StringComparer.Ordinal);
            foreach (var (key, write, inTree) in written)
            {
                if (!required.TryGetValue(write.Component.Name, out var needing) || missed.TryGetValue(key, out var known) && known.Count == 0)
                    continue;

                var found = new Dictionary<string, SortedSet<string>>(StringComparer.Ordinal);
                var taken = write.Method == null ? new HashSet<string>(StringComparer.Ordinal) : TakenOff(write, inTree, removes, adds);
                foreach (var (system, needs) in needing)
                {
                    var both = new SortedSet<string>(needs.Where(taken.Contains), StringComparer.Ordinal);
                    if (both.Count > 0)
                        found[system.Id] = both;
                }

                if (known == null)
                {
                    missed[key] = found;
                    continue;
                }
                foreach (var system in known.Keys.ToList())
                {
                    if (found.TryGetValue(system, out var also))
                        known[system].IntersectWith(also);
                    if (!found.ContainsKey(system) || known[system].Count == 0)
                        known.Remove(system);
                }
            }

            foreach (var pair in missed)
            {
                if (pair.Value.Count == 0 || !edges.TryGetValue(pair.Key, out var edge))
                    continue;
                foreach (var system in pair.Value.Keys.OrderBy(s => s, StringComparer.Ordinal))
                    if (edge.UnseenBy.All(u => u.System != system))
                        edge.MayMiss.Add(new MayMissReactor { System = system, Needs = "component:" + pair.Value[system].Min });
            }
        }

        /// <summary>
        /// The components the run of the write's method takes off an entity that may be the one
        /// written, and puts on nothing.
        /// </summary>
        HashSet<string> TakenOff(Access write, Func<IMethodSymbol, bool>? inTree, Dictionary<IMethodSymbol, List<Access>> removes,
            Dictionary<IMethodSymbol, HashSet<string>> adds)
        {
            var method = write.Method!;
            var run = _creations.Run(method).Where(m => inTree == null || inTree(m) || SymbolEqualityComparer.Default.Equals(m, method))
                .ToHashSet<IMethodSymbol>(SymbolEqualityComparer.Default);
            var added = new HashSet<string>(run.Where(adds.ContainsKey).SelectMany(m => adds[m]), StringComparer.Ordinal);
            var taken = new HashSet<string>(StringComparer.Ordinal);
            foreach (var removal in run.Where(removes.ContainsKey).SelectMany(m => removes[m]))
            {
                var name = removal.Component.Name;
                if (name == write.Component.Name || added.Contains(name) || taken.Contains(name))
                    continue;
                var visited = new HashSet<(IMethodSymbol, int)>();
                if (Lift(removal.Origin, method, run, visited).Any(o => !Different(o, write.Origin)) && !Apart(removal, write, method))
                    taken.Add(name);
            }
            return taken;
        }

        /// <summary>
        /// No call of <paramref name="method"/> runs both the write and the removal: the removal,
        /// or every call of the method's own leading to it, sits in a branch excluding the write.
        /// AdvancePhase takes Interacting off in `if (exiting) { …; return; }` and changes the
        /// phase after it.
        /// </summary>
        bool Apart(Access removal, Access write, IMethodSymbol method)
        {
            var writes = Sites(write);
            if (writes.Count == 0)
                return false;
            var anchors = SymbolEqualityComparer.Default.Equals(removal.Method, method)
                ? Sites(removal).Select(n => (SyntaxNode?)n).ToList()
                : _creations.Callees.TryGetValue(method, out var calls)
                    ? calls.Where(c => _context.Dispatch(new[] { c.Callee }).Any(m => _creations.Run(m).Contains(removal.Method!)))
                        .Select(c => (SyntaxNode?)c.Syntax)
                        .ToList()
                    : new List<SyntaxNode?>();
            return anchors.Count > 0 && anchors.All(a => a != null && writes.All(w => Exclusive(a, w)));
        }

        /// <summary>Where an access is in its method: the names on its line of generated members of its component; none when not found.</summary>
        List<SyntaxNode> Sites(Access access)
        {
            var declaration = access.Method?.DeclaringSyntaxReferences.FirstOrDefault()?.GetSyntax();
            if (declaration == null)
                return new List<SyntaxNode>();
            var tree = declaration.SyntaxTree;
            if (!_models.TryGetValue(tree, out var model))
                _models[tree] = model = _context.Full.GetSemanticModel(tree);
            return declaration.DescendantNodes()
                .OfType<SimpleNameSyntax>()
                .Where(n => _context.GeneratedMemberNames.Contains(n.Identifier.ValueText) &&
                            tree.GetLineSpan(n.Span).StartLinePosition.Line + 1 == access.Line &&
                            model.GetSymbolInfo(n).Symbol is { } symbol && _context.Classify(symbol) is { } api &&
                            api.Component.Name == access.Component.Name)
                .Cast<SyntaxNode>()
                .ToList();
        }

        /// <summary>
        /// Two places in one method that no single run of it reaches both of: the two branches of
        /// an if, two sections of a switch, the two arms of a conditional, or the first one in a
        /// branch ending in return or throw ahead of the other. Never across a loop or a lambda
        /// around both, which may run them one after the other, in a method with a goto, or with
        /// either in a finally.
        /// </summary>
        static bool Exclusive(SyntaxNode a, SyntaxNode b)
        {
            var above = new HashSet<SyntaxNode>(a.AncestorsAndSelf());
            var common = b.AncestorsAndSelf().FirstOrDefault(above.Contains);
            if (common == null || common == a || common == b)
                return false;
            var member = common.AncestorsAndSelf().OfType<MemberDeclarationSyntax>().FirstOrDefault();
            if (member == null || common.AncestorsAndSelf().TakeWhile(n => n != member).Any(Repeats) ||
                Below(a, common).Any(Deferred) || Below(b, common).Any(Deferred) ||
                a.Ancestors().Concat(b.Ancestors()).Any(n => n is FinallyClauseSyntax) ||
                member.DescendantNodes().Any(n => n is GotoStatementSyntax))
                return false;

            static bool Within(SyntaxNode node, SyntaxNode? part) => part != null && node.AncestorsAndSelf().Contains(part);
            switch (common)
            {
                case IfStatementSyntax @if:
                    return Within(a, @if.Statement) && Within(b, @if.Else) || Within(b, @if.Statement) && Within(a, @if.Else);
                case ConditionalExpressionSyntax conditional:
                    return Within(a, conditional.WhenTrue) && Within(b, conditional.WhenFalse) ||
                           Within(b, conditional.WhenTrue) && Within(a, conditional.WhenFalse);
                case SwitchStatementSyntax @switch:
                    var sa = @switch.Sections.FirstOrDefault(s => Within(a, s));
                    var sb = @switch.Sections.FirstOrDefault(s => Within(b, s));
                    return sa != null && sb != null && sa != sb;
            }

            var first = a.SpanStart < b.SpanStart ? a : b;
            return Below(first, common).Any(n =>
                n is StatementSyntax statement && (n.Parent is IfStatementSyntax owner && owner.Statement == n || n.Parent is ElseClauseSyntax) && Exits(statement) ||
                n is SwitchSectionSyntax section && section.Statements.Count > 0 && Exits(section.Statements.Last()));

            static IEnumerable<SyntaxNode> Below(SyntaxNode node, SyntaxNode top) => node.AncestorsAndSelf().TakeWhile(n => n != top);
            static bool Repeats(SyntaxNode n) => n is CommonForEachStatementSyntax or ForStatementSyntax or WhileStatementSyntax or DoStatementSyntax || Deferred(n);
            static bool Deferred(SyntaxNode n) => n is AnonymousFunctionExpressionSyntax or LocalFunctionStatementSyntax;
            static bool Exits(StatementSyntax s) =>
                s is ReturnStatementSyntax or ThrowStatementSyntax || s is BlockSyntax block && block.Statements.Count > 0 && Exits(block.Statements.Last());
        }

        /// <summary>
        /// Where the entity of a removal comes from as seen in <paramref name="method"/>, the one
        /// writing: a parameter of a method it calls is followed to what the calls of the run
        /// pass, any number of them; one no call of the run passes is not known.
        /// </summary>
        IEnumerable<EntityOrigin> Lift(EntityOrigin origin, IMethodSymbol method, HashSet<IMethodSymbol> run, HashSet<(IMethodSymbol, int)> visited)
        {
            if (origin.Method == null || SymbolEqualityComparer.Default.Equals(origin.Method, method))
            {
                yield return origin;
                yield break;
            }
            if (!visited.Add((origin.Method, origin.Parameter)))
                yield break;

            var sites = _creations.PassedTo(origin.Method, origin.Parameter).Where(s => run.Contains(s.Caller)).ToList();
            if (sites.Count == 0)
                yield return EntityOrigin.Unknown;
            foreach (var site in sites)
            foreach (var lifted in Lift(site.Origin, method, run, visited))
                yield return lifted;
        }

        /// <summary>Two entities known for certain to be different ones: both known, and not the same parameter, creation, group or indexed component.</summary>
        bool Different(EntityOrigin a, EntityOrigin b)
        {
            static bool Known(EntityOrigin o) => o.Created != null || o.Method != null || o.Group != null || o.Carries != null;
            if (!Known(a) || !Known(b))
                return false;
            if (a.Method != null || b.Method != null)
                return !(SymbolEqualityComparer.Default.Equals(a.Method, b.Method) && a.Parameter == b.Parameter);
            if (a.Created != null && b.Created != null)
                return !_creations.SameCreation(a.Created, b.Created);
            if (a.Group != null && b.Group != null)
                return !SymbolEqualityComparer.Default.Equals(a.Group, b.Group);
            if (a.Carries != null && b.Carries != null)
                return a.Carries != b.Carries;
            return true;
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
