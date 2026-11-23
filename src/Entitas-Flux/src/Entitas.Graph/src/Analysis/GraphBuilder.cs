using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;
using Entitas.SourceGenerator;
using Entitas.SourceGenerator.CodeGeneration;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Entitas.Graph.Analysis
{
    public sealed class GraphOptions
    {
        public string RequestSuffix = "Request";
        public string EventSuffix = "Event";

        /// <summary>How deep component access is followed through queries, factories and services.</summary>
        public int MaxCallDepth = 4;
    }

    /// <summary>
    /// Builds the ECS graph of one compilation from Roslyn semantics.
    ///
    /// The generated API is what connects systems to components (AddX, GameMatcher.X, isX…),
    /// so it is resolved as symbols, never matched as text: every generated member is mapped
    /// to its component through the generated file that declares it
    /// (<c>{Context}/Components/{Context}{X}Component.cs</c>). That is exact for a chained
    /// call on a return value (<c>CreateEntity.Event().AddX()</c>) and for a matcher read as a
    /// property inside AllOf — the two things a syntax-only graph cannot see.
    ///
    /// Works in both output modes: generated files already in the compilation are used as
    /// they are, otherwise the generator engine produces them in memory.
    /// </summary>
    public static class GraphBuilder
    {
        public static EcsGraph Build(Compilation compilation, string projectRoot, GraphOptions options)
        {
            var stopwatch = Stopwatch.StartNew();
            var context = Prepare(compilation, projectRoot, options);

            // Walked in parallel, but each result keeps its tree's place, in path order: everything
            // downstream (registration order, the `through` texts of inferred edges, which of
            // several equal sites an edge keeps) follows the order accesses arrive in. Collecting
            // them as they finished made two builds of the same code write different graph.json
            // files; following the csproj's order would tie them to however Unity lists the files.
            var trees = context.Full.SyntaxTrees.OrderBy(t => t.FilePath, StringComparer.Ordinal).ToArray();
            var walked = new TreeResult?[trees.Length];
            Parallel.For(0, trees.Length, i => walked[i] = new TreeWalker(context, trees[i]).Walk());
            var results = walked.Where(r => r != null).Select(r => r!).ToList();

            var graph = Assemble(context, results);
            graph.Metadata.BuildMilliseconds = stopwatch.ElapsedMilliseconds;
            graph.Metadata.SourceFiles = compilation.SyntaxTrees.Count();
            graph.Metadata.Project = compilation.AssemblyName ?? string.Empty;
            graph.Metadata.RequestSuffix = options.RequestSuffix;
            graph.Metadata.EventSuffix = options.EventSuffix;
            graph.Metadata.UnresolvedDestroys.AddRange(context.UnresolvedDestroys
                .Distinct()
                .OrderBy(u => u.File, StringComparer.Ordinal).ThenBy(u => u.Line).ThenBy(u => u.Accessor, StringComparer.Ordinal)
                .Select(u => new UnresolvedDestroy { Source = u.Accessor, File = u.File, Line = u.Line }));
            graph.Sort();
            return graph;
        }

        // -- preparation --------------------------------------------------------------

        static BuildContext Prepare(Compilation compilation, string projectRoot, GraphOptions options)
        {
            var input = GenerationInput.From(compilation, parallel: true);
            CodeGeneratorExtensions.ignoreNamespaces = input.Options.IgnoreNamespaces;

            var logical = new Dictionary<SyntaxTree, string>();
            Compilation full;
            var onDisk = GeneratedFiles.FindGeneratedTrees(compilation);
            if (onDisk.Count > 0)
            {
                full = compilation;
                var outputDirectory = GeneratedFiles.OutputDirectory(compilation, input.Options, null);
                foreach (var tree in onDisk)
                {
                    var name = outputDirectory == null ? null : GeneratedFiles.LogicalName(tree.FilePath, outputDirectory);
                    logical[tree] = Normalize(name ?? tree.FilePath);
                }
            }
            else
            {
                var parseOptions = compilation.SyntaxTrees.Select(t => t.Options).OfType<CSharpParseOptions>().FirstOrDefault();
                var trees = new List<SyntaxTree>();
                foreach (var source in EntitasGenerators.Generate(input).Sources)
                {
                    var name = Normalize(source.FileName);
                    var tree = CSharpSyntaxTree.ParseText(GeneratedFiles.SourceGeneratorText(source), parseOptions, path: "<entitas-generated>/" + name);
                    trees.Add(tree);
                    logical[tree] = name;
                }

                full = compilation.AddSyntaxTrees(trees);
            }

            var context = new BuildContext(full, projectRoot, options, logical);
            context.IndexComponents(input);
            context.IndexTypes();
            return context;
        }

        static string Normalize(string path) => path.Replace('\\', '/');

        // -- assembly of the graph --------------------------------------------------------

        static EcsGraph Assemble(BuildContext context, List<TreeResult> results)
        {
            var graph = new EcsGraph();
            var accesses = results.SelectMany(r => r.Accesses).ToList();
            var destroys = results.SelectMany(r => r.Destroys).ToList();
            var calls = results.SelectMany(r => r.Calls).ToList();
            var groupUses = results.SelectMany(r => r.GroupUses).ToList();
            var registrations = results.SelectMany(r => r.Registrations).ToList();

            // Components.
            foreach (var component in context.Components.Where(c => c.ChangeOf == null && c.ListenerOf == null))
                graph.Nodes.Add(component.ToNode(context));

            // Systems and features always; other types once they touch a component.
            var accessorIds = new HashSet<string>(accesses.Select(a => a.Accessor.Id).Concat(destroys.Select(d => d.Accessor.Id)), StringComparer.Ordinal);
            foreach (var type in context.Types.Values)
            {
                if (type.Kind == NodeKind.Type && (!accessorIds.Contains(type.Id) || type.Generated))
                    continue;
                graph.Nodes.Add(type.ToNode(context));
            }

            var nodeIds = new HashSet<string>(graph.Nodes.Select(n => n.Id), StringComparer.Ordinal);
            var edges = new Dictionary<string, GraphEdge>(StringComparer.Ordinal);

            // Per edge, the conditions of its sites — null once one site has none, since then the
            // write happens whenever the method runs — and the methods the system called to get
            // there. Sets, so the order sites arrive in changes nothing in graph.json.
            var conditions = new Dictionary<string, SortedSet<string>?>(StringComparer.Ordinal);
            var called = new Dictionary<string, SortedSet<string>>(StringComparer.Ordinal);

            void AddEdge(GraphEdge edge, IReadOnlyCollection<string>? condition = null, IReadOnlyCollection<string>? entries = null)
            {
                if (!nodeIds.Contains(edge.Source) || !nodeIds.Contains(edge.Target))
                    return;

                var key = edge.Key;
                if (edges.TryGetValue(key, out var existing))
                {
                    existing.Count += edge.Count;
                    if (string.CompareOrdinal(edge.SourceFile ?? string.Empty, existing.SourceFile ?? string.Empty) < 0 ||
                        (edge.SourceFile == existing.SourceFile && edge.Line < existing.Line))
                    {
                        existing.SourceFile = edge.SourceFile;
                        existing.Line = edge.Line;
                    }

                    if (condition == null)
                        conditions[key] = null;
                    else
                        conditions[key]?.UnionWith(condition);
                }
                else
                {
                    edges.Add(key, edge);
                    conditions[key] = condition == null ? null : new SortedSet<string>(condition, StringComparer.Ordinal);
                }

                if (entries != null)
                {
                    if (!called.TryGetValue(key, out var set))
                        called[key] = set = new SortedSet<string>(StringComparer.Ordinal);
                    set.UnionWith(entries);
                }
            }

            // Direct accesses.
            foreach (var access in accesses)
                AddEdge(access.ToEdge(Confidence.Extracted, null), Guards.One(access.Condition));

            // Groups: field/local symbol -> matcher shape.
            var groups = new Dictionary<ISymbol, GroupShape>(SymbolEqualityComparer.Default);
            foreach (var access in accesses.Where(a => a.MatcherGroup != null && a.Clause != null))
            {
                if (!groups.TryGetValue(access.MatcherGroup!, out var shape))
                    groups[access.MatcherGroup!] = shape = new GroupShape();
                shape.Add(access.Clause!, access.Component.Name);
                if (access.Relation == Relation.Reacts && !shape.Changes.Contains(access.Component.Name))
                    shape.Changes.Add(access.Component.Name);
            }

            // Destroy() on entities of a resolved group destroys what that group requires.
            foreach (var destroy in destroys)
            {
                if (destroy.Group == null || !groups.TryGetValue(destroy.Group, out var shape))
                {
                    context.UnresolvedDestroys.Add((destroy.Accessor.Id, destroy.File, destroy.Line));
                    continue;
                }

                var required = shape.AllOf.Count > 0 ? shape.AllOf : shape.AnyOf;
                foreach (var name in required)
                {
                    var component = context.ComponentByName(name);
                    if (component == null) continue;
                    AddEdge(new GraphEdge
                    {
                        Source = destroy.Accessor.Id,
                        Target = component.Id,
                        Relation = Relation.Destroys,
                        Via = "Destroy",
                        Confidence = shape.AllOf.Count > 0 ? Confidence.Extracted : Confidence.Inferred,
                        SourceFile = destroy.File,
                        Line = destroy.Line
                    }, Guards.One(destroy.Condition));
                }
            }

            // Features: Add(...) in source order.
            foreach (var group in registrations.GroupBy(r => r.Feature.Id))
            {
                var index = 0;
                foreach (var registration in group.OrderBy(r => r.File, StringComparer.Ordinal).ThenBy(r => r.Position))
                {
                    index++;
                    AddEdge(new GraphEdge
                    {
                        Source = registration.Feature.Id,
                        Target = registration.Child.Id,
                        Relation = Relation.Contains,
                        Via = "Add",
                        Order = index,
                        SourceFile = registration.File,
                        Line = registration.Line
                    });
                }
            }

            // Access through queries, factories and services, credited to the systems calling them.
            foreach (var (edge, condition, entries) in Propagate(context, accesses, destroys, calls, groupUses, groups))
                AddEdge(edge, condition, entries);

            // Destroyers of an entity shape destroy every component created on that shape.
            foreach (var (edge, condition) in InferDestroysFromShapes(context, accesses, destroys, groups))
                AddEdge(edge, Guards.One(condition));

            // ...and what a loop marks for them (isDestructed = true) carries along.
            var known = new HashSet<(string, string)>(edges.Values.Where(e => e.Relation == Relation.Destroys).Select(e => (e.Source, e.Target)));
            foreach (var (edge, condition) in InferDestroysFromMarking(context, accesses, destroys, groups, known))
                AddEdge(edge, Guards.One(condition));

            foreach (var pair in conditions)
                if (pair.Value != null)
                    edges[pair.Key].Condition = Guards.Format(pair.Value);
            foreach (var pair in called)
            {
                var edge = edges[pair.Key];
                if (pair.Value.Count != 1 || pair.Value.Min != edge.Through)
                    edge.Called = string.Join("; ", pair.Value);
            }

            // Flags set only while the entity carries a transient component (JustCreated). Group
            // shapes and loops exist only here, so the conclusion goes into the graph for the report.
            var transient = TransientComponents(context, accesses, groups);
            foreach (var node in graph.Nodes)
                if (transient.TryGetValue(node.Label, out var removers) && node.Kind == NodeKind.Component)
                    node.TransientRemovedBy.AddRange(removers);
            var guards = TransientGuards(context, accesses, groups, transient);
            foreach (var edge in edges.Values)
            {
                if (edge.Relation == Relation.Creates && edge.Confidence == Confidence.Extracted && edge.Entity == "group" &&
                    guards.TryGetValue((edge.Source, edge.Target), out var guard))
                    edge.OnlyWhile = guard;
            }

            graph.Edges.AddRange(edges.Values);
            graph.Nodes.Sort((a, b) => string.CompareOrdinal(a.Id, b.Id));
            FeatureTree.Place(graph);
            return graph;
        }

        /// <summary>
        /// Component access in non-system types (a query's group, a factory's AddX) is credited
        /// to every system that reaches it through calls — interface calls included, resolved to
        /// the implementations in the project — as INFERRED, naming the method it happens in, the
        /// methods the system called to get there, and the conditions it happens under.
        ///
        /// A write in a helper is conditional when the system's call is, when a call on the way
        /// is, or when the write is in the helper. The text shown is the one nearest the system:
        /// its call's condition is in the code the reader of a flow is looking at, and says which
        /// of the system's cases runs the factory (`request.isGhost`); the factory's own `if` is
        /// shown only when everything above it is unconditional. " &amp;&amp; …" marks the rest.
        /// A method called both under a condition and without one is reached unconditionally.
        /// </summary>
        static IEnumerable<(GraphEdge Edge, IReadOnlyCollection<string>? Condition, IReadOnlyCollection<string> Called)> Propagate(
            BuildContext context,
            List<Access> accesses,
            List<DestroySite> destroys,
            List<CallSite> calls,
            List<GroupUse> groupUses,
            Dictionary<ISymbol, GroupShape> groups)
        {
            // A query's matchers sit in its constructor; the methods using the group read them.
            var matchersByGroup = accesses
                .Where(a => a.MatcherGroup != null)
                .GroupBy<Access, ISymbol>(a => a.MatcherGroup!, SymbolEqualityComparer.Default)
                .ToDictionary<IGrouping<ISymbol, Access>, ISymbol, List<Access>>(g => g.Key, g => g.ToList(), SymbolEqualityComparer.Default);
            var groupsByMethod = groupUses
                .GroupBy<GroupUse, IMethodSymbol>(u => u.Method, SymbolEqualityComparer.Default)
                .ToDictionary<IGrouping<IMethodSymbol, GroupUse>, IMethodSymbol, List<ISymbol>>(
                    g => g.Key, g => g.Select(u => u.Group).Distinct(SymbolEqualityComparer.Default).ToList()!, SymbolEqualityComparer.Default);

            var byMethod = accesses
                .Where(a => a.Method != null && a.Accessor.Kind == NodeKind.Type)
                .GroupBy<Access, IMethodSymbol>(a => a.Method!, SymbolEqualityComparer.Default)
                .ToDictionary<IGrouping<IMethodSymbol, Access>, IMethodSymbol, List<Access>>(g => g.Key, g => g.ToList(), SymbolEqualityComparer.Default);

            var destroysByMethod = destroys
                .Where(d => d.Method != null && d.Accessor.Kind == NodeKind.Type)
                .GroupBy<DestroySite, IMethodSymbol>(d => d.Method!, SymbolEqualityComparer.Default)
                .ToDictionary<IGrouping<IMethodSymbol, DestroySite>, IMethodSymbol, List<DestroySite>>(g => g.Key, g => g.ToList(), SymbolEqualityComparer.Default);

            var callees = calls
                .GroupBy<CallSite, IMethodSymbol>(c => c.Caller, SymbolEqualityComparer.Default)
                .ToDictionary<IGrouping<IMethodSymbol, CallSite>, IMethodSymbol, List<CallSite>>(g => g.Key, g => g.ToList(), SymbolEqualityComparer.Default);
            var dispatched = new Dictionary<IMethodSymbol, List<IMethodSymbol>>(SymbolEqualityComparer.Default);
            List<IMethodSymbol> Dispatch(IMethodSymbol callee)
            {
                if (!dispatched.TryGetValue(callee, out var implementations))
                    dispatched[callee] = implementations = context.Dispatch(new[] { callee }).ToList();
                return implementations;
            }

            static string Name(IMethodSymbol method) => $"{method.ContainingType.Name}.{method.Name}";

            foreach (var system in context.Types.Values.Where(t => t.Kind == NodeKind.System || t.Kind == NodeKind.Feature))
            {
                // What the system reaches, breadth-first, each method once at its fewest calls.
                var roots = callees.Keys.Where(m => SymbolEqualityComparer.Default.Equals(m.ContainingType, system.Symbol)).ToList();
                var visited = new HashSet<IMethodSymbol>(SymbolEqualityComparer.Default);
                var reached = new List<(IMethodSymbol Method, int Depth)>();
                var depths = new Dictionary<IMethodSymbol, int>(SymbolEqualityComparer.Default);
                var frontier = roots.SelectMany(r => callees[r]).SelectMany(c => Dispatch(c.Callee)).ToList();
                for (var depth = 0; depth < context.Options.MaxCallDepth && frontier.Count > 0; depth++)
                {
                    var next = new List<IMethodSymbol>();
                    foreach (var method in frontier)
                    {
                        if (!visited.Add(method))
                            continue;

                        var owner = context.TypeOf(method.ContainingType);
                        if (owner == null || owner.Kind != NodeKind.Type)
                            continue;

                        reached.Add((method, depth));
                        depths[method] = depth;
                        if (callees.TryGetValue(method, out var further))
                            next.AddRange(further.SelectMany(c => Dispatch(c.Callee)));
                    }

                    frontier = next;
                }

                if (reached.Count == 0)
                    continue;

                // Under which conditions, and through which of its own calls, the system gets to each
                // method: the calls in its code first, then down the calls the walk followed, until
                // nothing changes (a method reached twice, the second time unconditionally).
                var paths = new Dictionary<IMethodSymbol, Path>(SymbolEqualityComparer.Default);
                bool Arrive(IMethodSymbol method, IReadOnlyCollection<string>? condition, IEnumerable<string> entries)
                {
                    if (!depths.ContainsKey(method))
                        return false;
                    if (!paths.TryGetValue(method, out var path))
                    {
                        paths[method] = new Path(condition, entries);
                        return true;
                    }
                    return path.Join(condition, entries);
                }

                foreach (var call in roots.SelectMany(r => callees[r]))
                    foreach (var method in Dispatch(call.Callee))
                        Arrive(method, Guards.One(call.Condition), new[] { Name(method) });

                for (var changed = true; changed;)
                {
                    changed = false;
                    foreach (var (method, depth) in reached)
                    {
                        if (depth + 1 >= context.Options.MaxCallDepth || !callees.TryGetValue(method, out var further) || !paths.TryGetValue(method, out var from))
                            continue;
                        foreach (var call in further)
                            foreach (var callee in Dispatch(call.Callee))
                                changed |= Arrive(callee, Guards.And(from.Condition, call.Condition), from.Entries);
                    }
                }

                foreach (var (method, _) in reached)
                {
                    var through = Name(method);
                    var path = paths.TryGetValue(method, out var known) ? known : new Path(null, new[] { through });
                    if (byMethod.TryGetValue(method, out var found))
                    {
                        // Only writes carry a condition: a read under one is still a read of the component.
                        foreach (var access in found)
                            yield return (access.ToEdge(Confidence.Inferred, through, system.Id),
                                access.Relation is Relation.Creates or Relation.Removes ? Guards.And(path.Condition, access.Condition) : null,
                                path.Entries);
                    }

                    if (groupsByMethod.TryGetValue(method, out var usedGroups))
                    {
                        foreach (var group in usedGroups)
                            if (matchersByGroup.TryGetValue(group, out var matchers))
                                foreach (var matcher in matchers)
                                    yield return (matcher.ToEdge(Confidence.Inferred, through, system.Id), null, path.Entries);
                    }

                    if (destroysByMethod.TryGetValue(method, out var destroyed))
                    {
                        foreach (var destroy in destroyed)
                        {
                            if (destroy.Group == null || !groups.TryGetValue(destroy.Group, out var shape))
                                continue;
                            foreach (var name in shape.AllOf)
                            {
                                var component = context.ComponentByName(name);
                                if (component == null) continue;
                                yield return (new GraphEdge
                                {
                                    Source = system.Id, Target = component.Id, Relation = Relation.Destroys, Via = "Destroy",
                                    Confidence = Confidence.Inferred, Through = through, SourceFile = destroy.File, Line = destroy.Line
                                }, Guards.And(path.Condition, destroy.Condition), path.Entries);
                            }
                        }
                    }
                }
            }
        }

        /// <summary>How a system gets to a method: the conditions on the way (null: none) and its own calls that lead there.</summary>
        sealed class Path
        {
            public SortedSet<string>? Condition;
            public readonly SortedSet<string> Entries = new SortedSet<string>(StringComparer.Ordinal);

            public Path(IReadOnlyCollection<string>? condition, IEnumerable<string> entries)
            {
                Condition = condition == null ? null : new SortedSet<string>(condition, StringComparer.Ordinal);
                Entries.UnionWith(entries);
            }

            /// <summary>Another way there: unconditional if either is; true when anything changed.</summary>
            public bool Join(IReadOnlyCollection<string>? condition, IEnumerable<string> entries)
            {
                var changed = false;
                if (Condition != null)
                {
                    if (condition == null)
                    {
                        Condition = null;
                        changed = true;
                    }
                    else
                    {
                        var before = Condition.Count;
                        Condition.UnionWith(condition);
                        changed = Condition.Count != before;
                    }
                }

                var count = Entries.Count;
                Entries.UnionWith(entries);
                return changed || Entries.Count != count;
            }
        }

        /// <summary>
        /// The components an entity carries are known where it is created: every Add/flag on
        /// the same fluent chain or local variable, plus what the factory it came from adds
        /// (CreateEntity.Event() sets isEvent). A system destroying a group whose components
        /// that entity carries — directly, or once another system adds them to entities like
        /// it (Ready, added to every Event) — destroys every component of that entity.
        /// </summary>
        static IEnumerable<(GraphEdge Edge, string? Condition)> InferDestroysFromShapes(
            BuildContext context,
            List<Access> accesses,
            List<DestroySite> destroys,
            Dictionary<ISymbol, GroupShape> groups)
        {
            var creates = accesses.Where(a => a.Relation == Relation.Creates && a.Component.ChangeOf == null).ToList();

            // What each factory method adds to the entity it returns.
            var factoryAdds = creates
                .Where(a => a.Method != null)
                .GroupBy<Access, IMethodSymbol>(a => a.Method!, SymbolEqualityComparer.Default)
                .ToDictionary<IGrouping<IMethodSymbol, Access>, IMethodSymbol, HashSet<string>>(g => g.Key, g => g.Select(a => a.Component.Name).ToHashSet(StringComparer.Ordinal), SymbolEqualityComparer.Default);

            var shapes = new List<HashSet<string>>();
            foreach (var site in creates.Where(a => a.Site != null).GroupBy(a => a.Site!))
            {
                var shape = site.Select(a => a.Component.Name).ToHashSet(StringComparer.Ordinal);
                foreach (var factory in site.Select(a => a.RootFactory).Where(f => f != null).Distinct(SymbolEqualityComparer.Default))
                {
                    if (factoryAdds.TryGetValue((IMethodSymbol)factory!, out var added))
                        shape.UnionWith(added);
                }

                shapes.Add(shape);
            }

            // Components systems add to entities of a group: (group requirement, component, by whom).
            var groupWrites = creates
                .Where(a => a.Group != null && groups.ContainsKey(a.Group))
                .Select(a => (Requires: groups[a.Group!].AllOf, Component: a.Component.Name, By: a.Accessor.Label))
                .Where(w => w.Requires.Count > 0)
                .ToList();

            foreach (var destroy in destroys)
            {
                if (destroy.Group == null || !groups.TryGetValue(destroy.Group, out var target) || target.AllOf.Count == 0)
                    continue;

                foreach (var shape in shapes)
                {
                    var carried = new HashSet<string>(shape, StringComparer.Ordinal);
                    var addedBy = new List<string>();
                    foreach (var write in groupWrites)
                    {
                        if (!carried.Contains(write.Component) && write.Requires.All(carried.Contains))
                        {
                            carried.Add(write.Component);
                            addedBy.Add($"{write.Component} by {write.By}");
                        }
                    }

                    if (!target.AllOf.All(carried.Contains))
                        continue;

                    var through = "created with " + string.Join(" + ", shape.OrderBy(s => s, StringComparer.Ordinal)) +
                                  (addedBy.Count > 0 ? "; " + string.Join(", ", addedBy.Distinct()) : string.Empty);

                    foreach (var name in shape.Where(n => !target.AllOf.Contains(n)))
                    {
                        var component = context.ComponentByName(name);
                        if (component == null) continue;
                        yield return (new GraphEdge
                        {
                            Source = destroy.Accessor.Id, Target = component.Id, Relation = Relation.Destroys, Via = "Destroy",
                            Confidence = Confidence.Inferred, Through = through, SourceFile = destroy.File, Line = destroy.Line
                        }, destroy.Condition);
                    }
                }
            }
        }

        /// <summary>
        /// Entities are also marked for a generic destroyer long after they were created: a loop
        /// over a group sets <c>isProcessingStoring = true</c> and <c>isDestructed = true</c> on
        /// the same entity, and a system destroying every Destructed entity destroys the flag
        /// with it. So when the writes of one loop include what a destroyer requires (all of its
        /// AllOf, the rest coming from the group the loop runs over), the destroyer is credited
        /// with the other components those entities carry: the ones written in the loop, and the
        /// ones the group requires (a helper marking <c>TimeIsUp</c> entities Destructed destroys
        /// TimeIsUp). Nothing is inferred across loops, groups or systems: there the entities
        /// marked are not known to be the ones carrying the flag. A component the loop also
        /// removes is not credited, and neither is a pair the shapes already gave.
        /// </summary>
        static IEnumerable<(GraphEdge Edge, string? Condition)> InferDestroysFromMarking(
            BuildContext context,
            List<Access> accesses,
            List<DestroySite> destroys,
            Dictionary<ISymbol, GroupShape> groups,
            HashSet<(string Source, string Target)> known)
        {
            var loops = accesses
                .Where(a => a.Loop != null && a.Component.ChangeOf == null && (a.Relation == Relation.Creates || a.Relation == Relation.Removes))
                .GroupBy(a => a.Loop!)
                .Select(loop =>
                {
                    var first = loop.First();
                    var required = first.Group != null && groups.TryGetValue(first.Group, out var shape)
                        ? shape.AllOf.Where(n => !shape.Changes.Contains(n) && !shape.NoneOf.Contains(n)).ToList()
                        : new List<string>();
                    return (
                        By: first.Accessor.Label,
                        Writes: loop.Where(a => a.Relation == Relation.Creates).Select(a => a.Component.Name).ToHashSet(StringComparer.Ordinal),
                        Removes: loop.Where(a => a.Relation == Relation.Removes).Select(a => a.Component.Name).ToHashSet(StringComparer.Ordinal),
                        Required: required);
                })
                .ToList();

            // One edge per destroyer and component, however many loops mark it: on a real project
            // dozens of systems mark entities requiring Id, and dozens of identical edges would
            // bury the one that matters.
            var found = new Dictionary<(string Source, string Target), (DestroySite Destroy, List<string> Through)>();
            foreach (var destroy in destroys)
            {
                if (destroy.Group == null || !groups.TryGetValue(destroy.Group, out var target) || target.AllOf.Count == 0)
                    continue;

                foreach (var loop in loops)
                {
                    var marked = target.AllOf.Where(loop.Writes.Contains).OrderBy(n => n, StringComparer.Ordinal).ToList();
                    if (marked.Count == 0)
                        continue;

                    var carried = new HashSet<string>(loop.Writes, StringComparer.Ordinal);
                    carried.UnionWith(loop.Required);
                    if (!target.AllOf.All(carried.Contains) || target.NoneOf.Any(carried.Contains))
                        continue;

                    foreach (var name in carried.Where(n => !target.AllOf.Contains(n) && !loop.Removes.Contains(n)))
                    {
                        var component = context.ComponentByName(name);
                        if (component == null || known.Contains((destroy.Accessor.Id, component.Id)))
                            continue;

                        var key = (destroy.Accessor.Id, component.Id);
                        if (!found.TryGetValue(key, out var entry))
                            found[key] = entry = (destroy, new List<string>());
                        entry.Through.Add(loop.Writes.Contains(name)
                            ? $"marked {string.Join(" + ", marked)} by {loop.By} together with {name}"
                            : $"marked {string.Join(" + ", marked)} by {loop.By}, whose group requires {name}");
                    }
                }
            }

            foreach (var pair in found)
            {
                var through = pair.Value.Through.Distinct().OrderBy(t => t, StringComparer.Ordinal).ToList();
                yield return (new GraphEdge
                {
                    Source = pair.Key.Source, Target = pair.Key.Target, Relation = Relation.Destroys, Via = "Destroy",
                    Confidence = Confidence.Inferred, SourceFile = pair.Value.Destroy.File, Line = pair.Value.Destroy.Line,
                    Through = through[0] + (through.Count > 1 ? $" (and {through.Count - 1} more)" : string.Empty)
                }, pair.Value.Destroy.Condition);
            }
        }

        /// <summary>
        /// Components no entity keeps: those with [Cleanup], and those a system takes off every
        /// entity holding them — RemoveX, isX = false or RemoveComponent(X), run for every entity
        /// of a loop over a group of X alone (UnmarkJustCreatedEntitiesSystem). The group may skip
        /// entities with NoneOf only when that same loop sets what it skips: those are the
        /// entities it already took X from (CreationProcessed). Anything less — a removal under a
        /// condition or through a helper, a group requiring more than X, a loop that also sets X
        /// (isX = condition) — leaves entities that keep X, so it does not count.
        /// Returns component name → the systems removing it that way (none for [Cleanup] alone).
        /// </summary>
        static Dictionary<string, List<string>> TransientComponents(BuildContext context, List<Access> accesses, Dictionary<ISymbol, GroupShape> groups)
        {
            var loopWrites = accesses
                .Where(a => a.Loop != null && a.Relation == Relation.Creates)
                .GroupBy(a => a.Loop!)
                .ToDictionary(g => g.Key, g => g.Select(a => a.Component.Name).ToHashSet(StringComparer.Ordinal), StringComparer.Ordinal);

            var transient = new Dictionary<string, List<string>>(StringComparer.Ordinal);
            foreach (var removal in accesses)
            {
                if (removal.Relation != Relation.Removes || !removal.EveryEntity || removal.Accessor.Kind != NodeKind.System ||
                    removal.Component.ChangeOf != null || removal.Group == null || !groups.TryGetValue(removal.Group, out var shape))
                    continue;

                var name = removal.Component.Name;
                var writes = loopWrites.TryGetValue(removal.Loop!, out var written) ? written : new HashSet<string>(StringComparer.Ordinal);
                if (shape.AllOf.Count != 1 || shape.AllOf[0] != name || shape.AnyOf.Count > 0 || shape.Changes.Count > 0 ||
                    writes.Contains(name) || !shape.NoneOf.All(writes.Contains))
                    continue;

                if (!transient.TryGetValue(name, out var removers))
                    transient[name] = removers = new List<string>();
                if (!removers.Contains(removal.Accessor.Id))
                    removers.Add(removal.Accessor.Id);
            }

            foreach (var removers in transient.Values)
                removers.Sort(StringComparer.Ordinal);
            foreach (var component in context.Components.Where(c => c.Attributes.Any(a => a.StartsWith("Cleanup", StringComparison.Ordinal))))
                if (!transient.ContainsKey(component.Name))
                    transient[component.Name] = new List<string>();
            return transient;
        }

        /// <summary>
        /// A flag a system sets in a loop over a group requiring a transient component is set
        /// while the entity carries that component and never again (AssignFloorFromSnapshotSystem
        /// sets FloorRestored on AllOf(Id, Floor, Tilemap, JustCreated)): a "done" marker as surely
        /// as one the setter leaves out with NoneOf. Only the group's AllOf counts, without the
        /// XChanged requirements (a changed entity need not carry the component), and only loops
        /// in the setter itself: every write of the flag it makes on existing entities must be
        /// such a loop, or the flag stays in "never cleared".
        /// Returns (setter id, flag id) → the id of the transient component, the first by name.
        /// </summary>
        static Dictionary<(string Source, string Target), string> TransientGuards(
            BuildContext context,
            List<Access> accesses,
            Dictionary<ISymbol, GroupShape> groups,
            Dictionary<string, List<string>> transient)
        {
            var guards = new Dictionary<(string, string), string>();
            var unguarded = new HashSet<(string, string)>();
            foreach (var write in accesses.Where(a => a.Relation == Relation.Creates && a.Component.Flag && a.Component.ChangeOf == null &&
                                                      a.Site == null && a.Group != null))
            {
                var key = (write.Accessor.Id, write.Component.Id);
                var guard = groups.TryGetValue(write.Group!, out var shape)
                    ? shape.AllOf.Where(n => n != write.Component.Name && !shape.Changes.Contains(n) && transient.ContainsKey(n))
                        .OrderBy(n => n, StringComparer.Ordinal).FirstOrDefault()
                    : null;

                if (guard == null)
                {
                    unguarded.Add(key);
                    guards.Remove(key);
                }
                else if (!unguarded.Contains(key) && context.ComponentByName(guard) is { } component)
                {
                    var id = component.Id;
                    guards[key] = guards.TryGetValue(key, out var other) && string.CompareOrdinal(other, id) < 0 ? other : id;
                }
            }

            return guards;
        }
    }

    // -- model used while building ------------------------------------------------------

    /// <summary>
    /// Conditions as they travel from sites to edges: null means none (the write happens whenever
    /// its method runs), otherwise the distinct texts, any of which may be the one that holds.
    /// </summary>
    static class Guards
    {
        /// <summary>Appended when further conditions enclose the one shown: they all hold together.</summary>
        public const string More = " && …";

        /// <summary>Edges name this many conditions, then count the rest.</summary>
        const int Named = 3;

        public static IReadOnlyCollection<string>? One(string? condition) => condition == null ? null : new[] { condition };

        /// <summary>
        /// <paramref name="inner"/> reached under <paramref name="outer"/>: the outer text stays —
        /// nearer the system, it is what tells its cases apart — with " &amp;&amp; …" for the inner.
        /// </summary>
        public static IReadOnlyCollection<string>? And(IReadOnlyCollection<string>? outer, string? inner) =>
            outer == null ? One(inner) : inner == null ? outer : outer.Select(t => t.EndsWith(More, StringComparison.Ordinal) ? t : t + More).ToList();

        /// <summary>
        /// "a or b or c or +2 more", ordinal; "x &amp;&amp; …" goes when "x" is there, since x
        /// covers it. Null when a condition and its negation are both there — the write is in the
        /// if and in the else (`if (x) AddX(a); else AddX(b);`), so it happens either way.
        /// </summary>
        public static string? Format(IReadOnlyCollection<string> conditions)
        {
            if (conditions.Any(t => Negations(t).Any(conditions.Contains)))
                return null;
            var texts = conditions
                .Where(t => !(t.EndsWith(More, StringComparison.Ordinal) && conditions.Contains(t.Substring(0, t.Length - More.Length))))
                .OrderBy(t => t, StringComparer.Ordinal)
                .ToList();
            return string.Join(" or ", texts.Take(Named)) + (texts.Count > Named ? $" or +{texts.Count - Named} more" : string.Empty);
        }

        /// <summary>
        /// How TreeWalker writes the negation of a condition it wrote as <paramref name="text"/>:
        /// `!x`, `!(a &lt; b)`, `a != b` for `a == b`. Only whole conditions: one cut short or
        /// followed by " &amp;&amp; …" has no exact negation.
        /// </summary>
        static IEnumerable<string> Negations(string text)
        {
            if (text.EndsWith("…", StringComparison.Ordinal))
                yield break;
            yield return "!" + text;
            yield return $"!({text})";
            if (text.IndexOf(" == ", StringComparison.Ordinal) is var eq && eq > 0 && text.IndexOf(" == ", eq + 1, StringComparison.Ordinal) < 0 && !text.Contains("&&") && !text.Contains("||"))
                yield return text.Substring(0, eq) + " != " + text.Substring(eq + 4);
            if (text.IndexOf(" != ", StringComparison.Ordinal) is var ne && ne > 0 && text.IndexOf(" != ", ne + 1, StringComparison.Ordinal) < 0 && !text.Contains("&&") && !text.Contains("||"))
                yield return text.Substring(0, ne) + " == " + text.Substring(ne + 4);
        }
    }

    sealed class ComponentInfo
    {
        public string Name = string.Empty;
        public string TypeName = string.Empty;
        public string[] Contexts = Array.Empty<string>();
        public bool Flag;
        public string FlagProperty = string.Empty;
        public List<string> Fields = new List<string>();
        public List<string> Attributes = new List<string>();
        public bool Generated;
        public string? ChangeOf;
        public string? ListenerOf;
        public string? SourceFile;
        public int Line;
        public string? Role;

        public string Id => "component:" + Name;

        public GraphNode ToNode(BuildContext context)
        {
            var node = new GraphNode
            {
                Id = Id, Label = Name, Kind = NodeKind.Component, FullName = TypeName, SourceFile = SourceFile, Line = Line,
                Generated = Generated, Flag = Flag, Role = Role
            };
            node.Contexts.AddRange(Contexts);
            node.Fields.AddRange(Fields);
            node.Attributes.AddRange(Attributes);
            return node;
        }
    }

    sealed class TypeInfo
    {
        public INamedTypeSymbol Symbol = null!;
        public string Id = string.Empty;
        public string Label = string.Empty;
        public string Kind = NodeKind.Type;
        public bool Generated;
        public List<string> Interfaces = new List<string>();
        public string? SourceFile;
        public int Line;

        public GraphNode ToNode(BuildContext context)
        {
            var node = new GraphNode
            {
                Id = Id, Label = Label, Kind = Kind, FullName = Symbol.ToDisplayString(), SourceFile = SourceFile, Line = Line, Generated = Generated
            };
            node.Interfaces.AddRange(Interfaces);
            return node;
        }
    }

    sealed class GroupShape
    {
        public readonly List<string> AllOf = new List<string>();
        public readonly List<string> AnyOf = new List<string>();
        public readonly List<string> NoneOf = new List<string>();

        /// <summary>
        /// [Watched] components whose XChanged marker the group matches. The marker is folded
        /// into the component, so it also sits in AllOf — but an entity of the group carries the
        /// marker, not necessarily the component (the change may have been its removal).
        /// </summary>
        public readonly List<string> Changes = new List<string>();

        public void Add(string clause, string component)
        {
            var list = clause switch
            {
                "AllOf" or "Matcher" => AllOf,
                "AnyOf" => AnyOf,
                "NoneOf" => NoneOf,
                _ => null
            };
            if (list != null && !list.Contains(component))
                list.Add(component);
        }
    }

    /// <summary>One use of the generated API by a type.</summary>
    sealed class Access
    {
        public TypeInfo Accessor = null!;
        public IMethodSymbol? Method;
        public ComponentInfo Component = null!;
        public string? Context;
        public string Relation = string.Empty;
        public string Via = string.Empty;
        public string File = string.Empty;
        public int Line;

        /// <summary>The entity this write lands on: a fluent chain or a local variable in one method.</summary>
        public string? Site;

        /// <summary>The source method the entity came from (a factory), when the chain starts with one.</summary>
        public ISymbol? RootFactory;

        /// <summary>The group whose foreach the written entity comes from.</summary>
        public ISymbol? Group;

        /// <summary>That foreach: writes sharing it land on the same entities.</summary>
        public string? Loop;

        /// <summary>
        /// For removals in a foreach of the accessor itself (not through a helper): the removal
        /// runs on every entity the loop visits — no condition, no jump before it.
        /// </summary>
        public bool EveryEntity;

        /// <summary>For matcher reads: the field or local the group is assigned to, and the clause.</summary>
        public ISymbol? MatcherGroup;
        public string? Clause;

        /// <summary>For writes: the condition it sits under in its method (TreeWalker.ConditionOf), null when none.</summary>
        public string? Condition;

        public GraphEdge ToEdge(string confidence, string? through, string? source = null)
        {
            var component = Component;
            var relation = Relation;
            return new GraphEdge
            {
                Source = source ?? Accessor.Id,
                Target = component.Id,
                Relation = relation,
                Via = Via,
                Confidence = confidence,
                Context = Context,
                Through = through,
                Entity = Site != null ? "new" : Group != null ? "group" : null,
                SourceFile = File,
                Line = Line
            };
        }
    }

    sealed class DestroySite
    {
        public TypeInfo Accessor = null!;
        public IMethodSymbol? Method;
        public ISymbol? Group;
        public string File = string.Empty;
        public int Line;
        public string? Condition;
    }

    sealed class CallSite
    {
        public IMethodSymbol Caller = null!;
        public IMethodSymbol Callee = null!;

        /// <summary>The condition the call sits under in the caller, null when none.</summary>
        public string? Condition;
    }

    sealed class RegistrationSite
    {
        public TypeInfo Feature = null!;
        public TypeInfo Child = null!;
        public string File = string.Empty;
        public int Line;
        public int Position;
    }

    sealed class GroupUse
    {
        public IMethodSymbol Method = null!;
        public ISymbol Group = null!;
    }

    sealed class TreeResult
    {
        public readonly List<GroupUse> GroupUses = new List<GroupUse>();
        public readonly List<Access> Accesses = new List<Access>();
        public readonly List<DestroySite> Destroys = new List<DestroySite>();
        public readonly List<CallSite> Calls = new List<CallSite>();
        public readonly List<RegistrationSite> Registrations = new List<RegistrationSite>();
    }
}
