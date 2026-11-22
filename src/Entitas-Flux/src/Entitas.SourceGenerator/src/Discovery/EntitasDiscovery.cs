using System.Collections.Generic;
using System.Linq;
using Entitas.SourceGenerator.CodeGeneration;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Entitas.SourceGenerator.Discovery
{
    public readonly struct DiscoveryResult
    {
        public readonly ComponentData[] Components;
        public readonly ContextData[] Contexts;
        public readonly EntityIndexData[] EntityIndices;
        public readonly CleanupData[] Cleanups;
        public readonly WatchedCleanupData[] WatchedCleanups;

        public DiscoveryResult(
            ComponentData[] components,
            ContextData[] contexts,
            EntityIndexData[] entityIndices,
            CleanupData[] cleanups,
            WatchedCleanupData[] watchedCleanups)
        {
            Components = components;
            Contexts = contexts;
            EntityIndices = entityIndices;
            Cleanups = cleanups;
            WatchedCleanups = watchedCleanups;
        }
    }

    /// <summary>
    /// Single discovery entry point. Runs the data providers over a set of
    /// <see cref="TypeSnapshot"/>s and returns the full data model.
    ///
    /// The incremental generator takes those snapshots per type declaration in the
    /// syntax pipeline; the Compilation overloads below take them by walking the
    /// assembly, which is what the CLIs and tests want.
    /// </summary>
    public static class EntitasDiscovery
    {
        public static DiscoveryResult Discover(Compilation compilation, bool ignoreNamespaces = false)
        {
            var resolver = ContextResolver.FromCompilation(compilation);
            var types = GetRelevantTypes(compilation).ToArray();
            return Discover(types, resolver, ignoreNamespaces);
        }

        public static DiscoveryResult Discover(INamedTypeSymbol[] types, ContextResolver resolver, bool ignoreNamespaces = false) =>
            Discover(types.Select(TypeSnapshot.From).ToArray(), resolver, ignoreNamespaces);

        public static DiscoveryResult Discover(TypeSnapshot[] types, ContextResolver resolver, bool ignoreNamespaces = false)
        {
            var components = new ComponentDataProvider(types, resolver, ignoreNamespaces).GetData();

            var contexts = resolver.ContextNames
                .Select(name =>
                {
                    var data = new ContextData();
                    data.SetContextName(name);
                    return data;
                })
                .ToArray();

            var entityIndices = new EntityIndexDataProvider(types, resolver, ignoreNamespaces).GetData();
            var cleanups = new CleanupDataProvider(types, resolver, ignoreNamespaces).GetData();
            var watchedCleanups = new WatchedCleanupDataProvider(types, resolver, ignoreNamespaces).GetData();

            return new DiscoveryResult(components, contexts, entityIndices, cleanups, watchedCleanups);
        }

        /// <summary>
        /// Collects all named types declared in the compilation's source assembly, except
        /// the ones Entitas-Flux itself wrote to disk (<c>Output = GenerationOutput.Files</c>).
        ///
        /// Those must never be discovered: generated code declares components of its own —
        /// the <c>{X}Changed</c> markers, event listener components, wrappers for
        /// <c>[Game]</c>-attributed plain classes — and feeding them back into discovery
        /// would generate every one of them a second time.
        /// </summary>
        public static IEnumerable<INamedTypeSymbol> GetCandidateTypes(Compilation compilation)
        {
            var all = GetAllTypes(compilation.Assembly.GlobalNamespace);

            var generated = GeneratedFiles.FindGeneratedTrees(compilation);
            if (generated.Count == 0)
                return all;

            var generatedTrees = new HashSet<SyntaxTree>(generated);
            return all.Where(type => !IsDeclaredOnlyIn(type, generatedTrees));
        }

        /// <summary>
        /// The candidate types discovery can draw data from: components, and types carrying
        /// attributes (a <c>[Game]</c> plain class gets a generated component, a
        /// <c>[CustomEntityIndex]</c> class an index). Every other type yields nothing, so
        /// it is not snapshotted at all — on a ~2000-file project that is most of them, and
        /// snapshotting means binding every public member's type.
        /// </summary>
        public static IEnumerable<INamedTypeSymbol> GetRelevantTypes(Compilation compilation) =>
            GetCandidateTypes(compilation).Where(IsRelevant);

        /// <summary>
        /// Same, checking types on every core when <paramref name="parallel"/> — deciding
        /// whether a type is a component binds its base types, the bulk of a cold run.
        /// </summary>
        public static IEnumerable<INamedTypeSymbol> GetRelevantTypes(Compilation compilation, bool parallel) =>
            parallel
                ? GetCandidateTypes(compilation).ToArray().AsParallel().AsOrdered().Where(IsRelevant).ToArray()
                : GetRelevantTypes(compilation);

        static bool IsRelevant(INamedTypeSymbol type)
        {
            foreach (var reference in type.DeclaringSyntaxReferences)
            {
                if (reference.GetSyntax() is MemberDeclarationSyntax { AttributeLists.Count: > 0 })
                    return true;
            }

            foreach (var @interface in type.AllInterfaces)
            {
                if (@interface.Name == "IComponent" &&
                    @interface.ContainingNamespace is { Name: "Entitas" } ns &&
                    ns.ContainingNamespace is { IsGlobalNamespace: true })
                    return true;
            }

            return false;
        }

        static bool IsDeclaredOnlyIn(INamedTypeSymbol type, HashSet<SyntaxTree> trees)
        {
            var references = type.DeclaringSyntaxReferences;
            if (references.Length == 0)
                return false;

            foreach (var reference in references)
            {
                if (!trees.Contains(reference.SyntaxTree))
                    return false;
            }

            return true;
        }

        static IEnumerable<INamedTypeSymbol> GetAllTypes(INamespaceSymbol root)
        {
            foreach (var type in root.GetTypeMembers())
            {
                foreach (var nested in GetAllTypesAndNested(type))
                    yield return nested;
            }

            foreach (var childNs in root.GetNamespaceMembers())
            {
                foreach (var type in GetAllTypes(childNs))
                    yield return type;
            }
        }

        static IEnumerable<INamedTypeSymbol> GetAllTypesAndNested(INamedTypeSymbol type)
        {
            yield return type;
            foreach (var nested in type.GetTypeMembers())
            {
                foreach (var inner in GetAllTypesAndNested(nested))
                    yield return inner;
            }
        }
    }
}
