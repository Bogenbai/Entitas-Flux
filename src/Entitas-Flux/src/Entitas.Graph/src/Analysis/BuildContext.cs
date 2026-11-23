using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Entitas.SourceGenerator;
using Entitas.SourceGenerator.CodeGeneration;
using Microsoft.CodeAnalysis;

namespace Entitas.Graph.Analysis
{
    /// <summary>What a member of the generated API means for its component.</summary>
    enum ApiKind { Add, Replace, Remove, SafeRemove, Set, Has, Get, Flag, Atomic, Matcher, Index, EntityIndex }

    /// <summary>Everything the tree walkers share: the compilation, the component index, the type index.</summary>
    sealed class BuildContext
    {
        public readonly Compilation Full;
        public readonly string ProjectRoot;
        public readonly GraphOptions Options;
        public readonly Dictionary<SyntaxTree, string> GeneratedTrees;

        public readonly List<ComponentInfo> Components = new List<ComponentInfo>();
        public readonly Dictionary<INamedTypeSymbol, TypeInfo> Types = new Dictionary<INamedTypeSymbol, TypeInfo>(SymbolEqualityComparer.Default);
        public readonly HashSet<string> GeneratedMemberNames = new HashSet<string>(StringComparer.Ordinal);
        public readonly ConcurrentBag<(string Accessor, string File, int Line)> UnresolvedDestroys = new ConcurrentBag<(string, string, int)>();

        readonly Dictionary<string, ComponentInfo> _byName = new Dictionary<string, ComponentInfo>(StringComparer.Ordinal);
        readonly Dictionary<string, (ComponentInfo Component, string Context)> _componentFiles = new Dictionary<string, (ComponentInfo, string)>(StringComparer.Ordinal);
        readonly Dictionary<string, string> _lookupFiles = new Dictionary<string, string>(StringComparer.Ordinal);
        readonly Dictionary<string, ComponentInfo> _entityIndexMethods = new Dictionary<string, ComponentInfo>(StringComparer.Ordinal);
        readonly Dictionary<IMethodSymbol, List<IMethodSymbol>> _implementations = new Dictionary<IMethodSymbol, List<IMethodSymbol>>(SymbolEqualityComparer.Default);

        public INamedTypeSymbol? EntityBase { get; private set; }

        public BuildContext(Compilation full, string projectRoot, GraphOptions options, Dictionary<SyntaxTree, string> generatedTrees)
        {
            Full = full;
            ProjectRoot = projectRoot;
            Options = options;
            GeneratedTrees = generatedTrees;
        }

        public ComponentInfo? ComponentByName(string name) => _byName.TryGetValue(name, out var c) ? c : null;

        public TypeInfo? TypeOf(INamedTypeSymbol? symbol)
        {
            if (symbol == null) return null;
            return Types.TryGetValue(symbol.OriginalDefinition, out var type) ? type : null;
        }

        public bool IsGenerated(SyntaxTree? tree) => tree != null && GeneratedTrees.ContainsKey(tree);

        public string RelativePath(string? path)
        {
            if (string.IsNullOrEmpty(path)) return string.Empty;
            if (path!.StartsWith("<entitas-generated>/", StringComparison.Ordinal)) return path;
            try
            {
                var relative = Path.GetRelativePath(ProjectRoot, path);
                return (relative.StartsWith("..", StringComparison.Ordinal) ? path : relative).Replace('\\', '/');
            }
            catch (ArgumentException)
            {
                return path.Replace('\\', '/');
            }
        }

        // -- components ---------------------------------------------------------------------

        public void IndexComponents(GenerationInput input)
        {
            var data = input.Data;
            var cleanups = data.OfType<CleanupData>().ToDictionary(c => c.componentData.GetTypeName(), c => c.cleanupMode.ToString(), StringComparer.Ordinal);
            var indexed = new Dictionary<string, List<string>>(StringComparer.Ordinal);
            foreach (var index in data.OfType<EntityIndexData>())
            {
                if (index.IsCustom()) continue;
                var type = index.GetComponentType();
                if (!indexed.TryGetValue(type, out var names)) indexed[type] = names = new List<string>();
                names.Add(index.GetEntityIndexName());
                if (!names.Contains(index.GetEntityIndexType().Contains("Primary") ? "PrimaryEntityIndex" : "EntityIndex"))
                    names.Add(index.GetEntityIndexType().Contains("Primary") ? "PrimaryEntityIndex" : "EntityIndex");
            }

            var componentData = data.OfType<ComponentData>().ToList();
            foreach (var d in componentData)
            {
                var name = d.ComponentName();
                if (_byName.ContainsKey(name)) continue;

                var symbol = Full.GetTypeByMetadataName(d.GetTypeName());
                var location = symbol?.Locations.FirstOrDefault(l => l.IsInSource && !IsGenerated(l.SourceTree));
                var info = new ComponentInfo
                {
                    Name = name,
                    TypeName = d.GetTypeName(),
                    Contexts = d.GetContextNames(),
                    Flag = d.GetMemberData().Length == 0,
                    FlagProperty = d.PrefixedComponentName(),
                    Generated = location == null,
                    SourceFile = location == null ? null : RelativePath(location.SourceTree!.FilePath),
                    Line = location == null ? 0 : location.GetLineSpan().StartLinePosition.Line + 1
                };
                info.Fields.AddRange(d.GetMemberData().Select(m => $"{m.type} {m.name}"));

                if (d.IsUnique()) info.Attributes.Add("Unique");
                if (d.ShouldWatchChanges()) info.Attributes.Add("Watched");
                if (d.IsEvent()) info.Attributes.Add("Event");
                if (cleanups.TryGetValue(d.GetTypeName(), out var mode)) info.Attributes.Add($"Cleanup({mode})");
                if (indexed.TryGetValue(d.GetTypeName(), out var indexNames))
                    info.Attributes.AddRange(indexNames.Where(n => n.EndsWith("EntityIndex", StringComparison.Ordinal)));
                if (info.Flag && d.GetFlagPrefix() != "is") info.Attributes.Add($"FlagPrefix({d.GetFlagPrefix()})");

                if (!info.Generated)
                {
                    // A component named exactly like the suffix (`Event`) is the marker every event
                    // carries, not an event of its own.
                    if (name.EndsWith(Options.RequestSuffix, StringComparison.Ordinal) && name != Options.RequestSuffix) info.Role = "request";
                    else if (name.EndsWith(Options.EventSuffix, StringComparison.Ordinal) && name != Options.EventSuffix) info.Role = "event";
                }

                Components.Add(info);
                _byName[name] = info;
            }

            // Generated companions: the XChanged marker of a [Watched] component, the listener of an [Event].
            foreach (var d in componentData)
            {
                var name = d.ComponentName();
                if (d.ShouldWatchChanges() && _byName.TryGetValue(d.TrackingChangesComponentName(), out var marker))
                    marker.ChangeOf = name;

                if (d.IsEvent())
                {
                    foreach (var eventData in d.GetEventData())
                    foreach (var contextName in d.GetContextNames())
                    {
                        if (_byName.TryGetValue(d.EventListener(contextName, eventData), out var listener))
                            listener.ListenerOf = name;
                    }
                }
            }

            foreach (var component in Components)
            foreach (var contextName in component.Contexts)
                _componentFiles[$"{contextName}/Components/{(contextName + component.Name).AddComponentSuffix()}.cs"] = (component, contextName);

            foreach (var contextName in input.ContextNames)
                _lookupFiles[$"{contextName}/{contextName}ComponentsLookup.cs"] = contextName;

            foreach (var index in data.OfType<EntityIndexData>())
            {
                if (index.IsCustom()) continue;
                var component = Components.FirstOrDefault(c => c.TypeName == index.GetComponentType());
                if (component == null) continue;
                _entityIndexMethods["GetEntityWith" + index.GetEntityIndexName()] = component;
                _entityIndexMethods["GetEntitiesWith" + index.GetEntityIndexName()] = component;
            }

            foreach (var tree in GeneratedTrees.Keys)
            {
                foreach (var node in tree.GetRoot().DescendantNodes())
                {
                    switch (node)
                    {
                        case Microsoft.CodeAnalysis.CSharp.Syntax.MethodDeclarationSyntax m: GeneratedMemberNames.Add(m.Identifier.ValueText); break;
                        case Microsoft.CodeAnalysis.CSharp.Syntax.PropertyDeclarationSyntax p: GeneratedMemberNames.Add(p.Identifier.ValueText); break;
                        case Microsoft.CodeAnalysis.CSharp.Syntax.VariableDeclaratorSyntax { Parent.Parent: Microsoft.CodeAnalysis.CSharp.Syntax.FieldDeclarationSyntax } v: GeneratedMemberNames.Add(v.Identifier.ValueText); break;
                    }
                }
            }
        }

        /// <summary>
        /// Which component a generated member belongs to, and what it does to it. Decided by the
        /// generated file that declares the member — never by a name that merely looks right.
        /// </summary>
        public (ComponentInfo Component, string Context, ApiKind Kind)? Classify(ISymbol symbol)
        {
            var declaration = symbol.OriginalDefinition.DeclaringSyntaxReferences.FirstOrDefault();
            if (declaration == null || !GeneratedTrees.TryGetValue(declaration.SyntaxTree, out var file))
                return null;

            var name = symbol.Name;
            if (_componentFiles.TryGetValue(file, out var owner))
            {
                var c = owner.Component;
                var n = c.Name;
                var lower = n.ToLowerFirst();
                var containing = symbol.ContainingType?.Name ?? string.Empty;

                if (symbol is IPropertySymbol && containing.EndsWith("Matcher", StringComparison.Ordinal) && name == n)
                    return (c, owner.Context, ApiKind.Matcher);

                if (symbol is IMethodSymbol)
                {
                    if (name == "Add" + n) return (c, owner.Context, ApiKind.Add);
                    if (name == "Replace" + n) return (c, owner.Context, ApiKind.Replace);
                    if (name == "Remove" + n) return (c, owner.Context, ApiKind.Remove);
                    if (name == "SafeRemove" + n) return (c, owner.Context, ApiKind.SafeRemove);
                    if (name == "Set" + n) return (c, owner.Context, ApiKind.Set);
                    return null;
                }

                if (symbol is IPropertySymbol)
                {
                    if (name == "has" + n) return (c, owner.Context, ApiKind.Has);
                    if (c.Flag && name == c.FlagProperty) return (c, owner.Context, ApiKind.Flag);
                    if (name == lower || name == lower + "Entity" || name == c.FlagProperty + "Entity") return (c, owner.Context, ApiKind.Get);
                    if (name == n) return (c, owner.Context, ApiKind.Atomic);
                }

                return null;
            }

            if (_lookupFiles.TryGetValue(file, out var lookupContext) && symbol is IFieldSymbol && _byName.TryGetValue(name, out var indexed))
                return (indexed, lookupContext, ApiKind.Index);

            if (symbol is IMethodSymbol && _entityIndexMethods.TryGetValue(name, out var byIndex))
                return (byIndex, byIndex.Contexts.FirstOrDefault() ?? string.Empty, ApiKind.EntityIndex);

            return null;
        }

        // -- types --------------------------------------------------------------------------

        /// <summary>
        /// Whether <paramref name="method"/> is a system's Execute() or Cleanup(): what Entitas
        /// calls every frame. Initialize and TearDown run once, a helper whenever it is called.
        /// </summary>
        public bool RunsEveryFrame(IMethodSymbol method)
        {
            if (method.Parameters.Length != 0 || method.Name is not ("Execute" or "Cleanup") && method.ExplicitInterfaceImplementations.Length == 0)
                return false;
            foreach (var frame in new[] { _execute, _cleanup })
                if (frame != null && SymbolEqualityComparer.Default.Equals(method.ContainingType.FindImplementationForInterfaceMember(frame), method))
                    return true;
            return false;
        }

        IMethodSymbol? _execute, _cleanup;

        /// <summary>The conditions a helper runs its delegate parameter under (Analysis.HelperConditions), worked out once per helper and parameter.</summary>
        public readonly ConcurrentDictionary<(IMethodSymbol Method, string Parameter), IReadOnlyList<(string Parameter, bool Negated)>?> HelperConditions =
            new ConcurrentDictionary<(IMethodSymbol, string), IReadOnlyList<(string, bool)>?>(new MethodParameterComparer());

        sealed class MethodParameterComparer : IEqualityComparer<(IMethodSymbol Method, string Parameter)>
        {
            public bool Equals((IMethodSymbol Method, string Parameter) x, (IMethodSymbol Method, string Parameter) y) =>
                SymbolEqualityComparer.Default.Equals(x.Method, y.Method) && string.Equals(x.Parameter, y.Parameter, StringComparison.Ordinal);

            public int GetHashCode((IMethodSymbol Method, string Parameter) obj) =>
                SymbolEqualityComparer.Default.GetHashCode(obj.Method) * 31 + StringComparer.Ordinal.GetHashCode(obj.Parameter);
        }

        public void IndexTypes()
        {
            var systems = Full.GetTypeByMetadataName("Entitas.Systems");
            var iSystem = Full.GetTypeByMetadataName("Entitas.ISystem");
            EntityBase = Full.GetTypeByMetadataName("Entitas.Entity");
            _execute = Full.GetTypeByMetadataName("Entitas.IExecuteSystem")?.GetMembers("Execute").OfType<IMethodSymbol>().FirstOrDefault(m => m.Parameters.Length == 0);
            _cleanup = Full.GetTypeByMetadataName("Entitas.ICleanupSystem")?.GetMembers("Cleanup").OfType<IMethodSymbol>().FirstOrDefault(m => m.Parameters.Length == 0);

            foreach (var type in AllTypes(Full.Assembly.GlobalNamespace))
            {
                if (type.TypeKind != TypeKind.Class && type.TypeKind != TypeKind.Struct)
                    continue;

                var declarations = type.DeclaringSyntaxReferences;
                if (declarations.Length == 0)
                    continue;

                var generated = declarations.All(r => IsGenerated(r.SyntaxTree));
                var isFeature = systems != null && DerivesFrom(type, systems);
                var isSystem = !isFeature && iSystem != null && type.AllInterfaces.Any(i => SymbolEqualityComparer.Default.Equals(i, iSystem));

                // The generated Feature base class is plumbing, not a feature of the project.
                if (isFeature && generated && type.Name == "Feature" && type.ContainingNamespace.IsGlobalNamespace)
                    continue;
                if ((isFeature || isSystem) && type.IsAbstract)
                    continue;

                var kind = isFeature ? NodeKind.Feature : isSystem ? NodeKind.System : NodeKind.Type;
                var location = type.Locations.FirstOrDefault(l => l.IsInSource && !IsGenerated(l.SourceTree)) ?? type.Locations.FirstOrDefault(l => l.IsInSource);
                var info = new TypeInfo
                {
                    Symbol = type,
                    Kind = kind,
                    Id = kind + ":" + type.ToDisplayString(),
                    Label = type.Name,
                    Generated = generated,
                    SourceFile = location?.SourceTree == null ? null : RelativePath(location.SourceTree.FilePath),
                    Line = location == null ? 0 : location.GetLineSpan().StartLinePosition.Line + 1
                };

                if (isSystem)
                {
                    foreach (var i in type.AllInterfaces)
                    {
                        if (i.ContainingNamespace?.ToDisplayString() != "Entitas") continue;
                        var shortName = i.Name switch
                        {
                            "IInitializeSystem" => "Initialize",
                            "IExecuteSystem" => "Execute",
                            "ICleanupSystem" => "Cleanup",
                            "ITearDownSystem" => "TearDown",
                            "IReactiveSystem" => "Reactive",
                            _ => null
                        };
                        if (shortName != null && !info.Interfaces.Contains(shortName))
                            info.Interfaces.Add(shortName);
                    }
                    info.Interfaces.Sort(StringComparer.Ordinal);
                }

                Types[type] = info;

                // Interface members implemented in the project, for resolving calls through interfaces.
                if (!generated)
                {
                    foreach (var @interface in type.AllInterfaces)
                    {
                        if (!@interface.Locations.Any(l => l.IsInSource)) continue;
                        foreach (var member in @interface.GetMembers().OfType<IMethodSymbol>())
                        {
                            if (type.FindImplementationForInterfaceMember(member) is IMethodSymbol implementation)
                                Add(member.OriginalDefinition, implementation);
                        }
                    }

                    foreach (var method in type.GetMembers().OfType<IMethodSymbol>())
                    {
                        for (var overridden = method.OverriddenMethod; overridden != null; overridden = overridden.OverriddenMethod)
                            Add(overridden.OriginalDefinition, method);
                    }
                }
            }
        }

        void Add(IMethodSymbol declared, IMethodSymbol implementation)
        {
            if (!_implementations.TryGetValue(declared, out var list))
                _implementations[declared] = list = new List<IMethodSymbol>();
            if (!list.Contains(implementation, SymbolEqualityComparer.Default))
                list.Add(implementation);
        }

        /// <summary>The methods a call can land in: itself when it has a body, plus project implementations and overrides.</summary>
        public IEnumerable<IMethodSymbol> Dispatch(IEnumerable<IMethodSymbol> callees)
        {
            foreach (var callee in callees)
            {
                var definition = callee.OriginalDefinition;
                if (!definition.IsAbstract && definition.DeclaringSyntaxReferences.Length > 0)
                    yield return definition;
                if (_implementations.TryGetValue(definition, out var implementations))
                    foreach (var implementation in implementations)
                        yield return implementation.OriginalDefinition;
            }
        }

        static bool DerivesFrom(INamedTypeSymbol type, INamedTypeSymbol baseType)
        {
            for (var current = type.BaseType; current != null; current = current.BaseType)
                if (SymbolEqualityComparer.Default.Equals(current, baseType)) return true;
            return false;
        }

        static IEnumerable<INamedTypeSymbol> AllTypes(INamespaceSymbol ns)
        {
            foreach (var type in ns.GetTypeMembers())
            foreach (var nested in WithNested(type))
                yield return nested;
            foreach (var child in ns.GetNamespaceMembers())
            foreach (var type in AllTypes(child))
                yield return type;
        }

        static IEnumerable<INamedTypeSymbol> WithNested(INamedTypeSymbol type)
        {
            yield return type;
            foreach (var nested in type.GetTypeMembers())
            foreach (var inner in WithNested(nested))
                yield return inner;
        }
    }
}
