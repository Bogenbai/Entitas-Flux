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

            void AddEdge(GraphEdge edge)
            {
                if (!nodeIds.Contains(edge.Source) || !nodeIds.Contains(edge.Target))
                    return;

                if (edges.TryGetValue(edge.Key, out var existing))
                {
                    existing.Count += edge.Count;
                    if (string.CompareOrdinal(edge.SourceFile ?? string.Empty, existing.SourceFile ?? string.Empty) < 0 ||
                        (edge.SourceFile == existing.SourceFile && edge.Line < existing.Line))
                    {
                        existing.SourceFile = edge.SourceFile;
                        existing.Line = edge.Line;
                    }
                }
                else
                {
                    edges.Add(edge.Key, edge);
                }
            }

            // Direct accesses.
            foreach (var access in accesses)
                AddEdge(access.ToEdge(Confidence.Extracted, null));

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
                    });
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
            foreach (var edge in Propagate(context, accesses, destroys, calls, groupUses, groups))
                AddEdge(edge);

            // Destroyers of an entity shape destroy every component created on that shape.
            foreach (var edge in InferDestroysFromShapes(context, accesses, destroys, groups))
                AddEdge(edge);

            // ...and what a loop marks for them (isDestructed = true) carries along.
            var known = new HashSet<(string, string)>(edges.Values.Where(e => e.Relation == Relation.Destroys).Select(e => (e.Source, e.Target)));
            foreach (var edge in InferDestroysFromMarking(context, accesses, destroys, groups, known))
                AddEdge(edge);

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
        /// the implementations in the project — as INFERRED, naming the method it happens in.
        /// </summary>
        static IEnumerable<GraphEdge> Propagate(
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
                .ToDictionary<IGrouping<IMethodSymbol, CallSite>, IMethodSymbol, List<IMethodSymbol>>(g => g.Key, g => g.Select(c => c.Callee).ToList(), SymbolEqualityComparer.Default);

            foreach (var system in context.Types.Values.Where(t => t.Kind == NodeKind.System || t.Kind == NodeKind.Feature))
            {
                var visited = new HashSet<IMethodSymbol>(SymbolEqualityComparer.Default);
                var frontier = new List<IMethodSymbol>();
                foreach (var method in callees.Keys.Where(m => SymbolEqualityComparer.Default.Equals(m.ContainingType, system.Symbol)))
                    frontier.AddRange(context.Dispatch(callees[method]));

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

                        var through = $"{method.ContainingType.Name}.{method.Name}";
                        if (byMethod.TryGetValue(method, out var found))
                        {
                            foreach (var access in found)
                                yield return access.ToEdge(Confidence.Inferred, through, system.Id);
                        }

                        if (groupsByMethod.TryGetValue(method, out var usedGroups))
                        {
                            foreach (var group in usedGroups)
                                if (matchersByGroup.TryGetValue(group, out var matchers))
                                    foreach (var matcher in matchers)
                                        yield return matcher.ToEdge(Confidence.Inferred, through, system.Id);
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
                                    yield return new GraphEdge
                                    {
                                        Source = system.Id, Target = component.Id, Relation = Relation.Destroys, Via = "Destroy",
                                        Confidence = Confidence.Inferred, Through = through, SourceFile = destroy.File, Line = destroy.Line
                                    };
                                }
                            }
                        }

                        if (callees.TryGetValue(method, out var further))
                            next.AddRange(context.Dispatch(further));
                    }

                    frontier = next;
                }
            }
        }

        /// <summary>
        /// The components an entity carries are known where it is created: every Add/flag on
        /// the same fluent chain or local variable, plus what the factory it came from adds
        /// (CreateEntity.Event() sets isEvent). A system destroying a group whose components
        /// that entity carries — directly, or once another system adds them to entities like
        /// it (Ready, added to every Event) — destroys every component of that entity.
        /// </summary>
        static IEnumerable<GraphEdge> InferDestroysFromShapes(
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
                        yield return new GraphEdge
                        {
                            Source = destroy.Accessor.Id, Target = component.Id, Relation = Relation.Destroys, Via = "Destroy",
                            Confidence = Confidence.Inferred, Through = through, SourceFile = destroy.File, Line = destroy.Line
                        };
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
        static IEnumerable<GraphEdge> InferDestroysFromMarking(
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
                yield return new GraphEdge
                {
                    Source = pair.Key.Source, Target = pair.Key.Target, Relation = Relation.Destroys, Via = "Destroy",
                    Confidence = Confidence.Inferred, SourceFile = pair.Value.Destroy.File, Line = pair.Value.Destroy.Line,
                    Through = through[0] + (through.Count > 1 ? $" (and {through.Count - 1} more)" : string.Empty)
                };
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
    }

    sealed class CallSite
    {
        public IMethodSymbol Caller = null!;
        public IMethodSymbol Callee = null!;
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
