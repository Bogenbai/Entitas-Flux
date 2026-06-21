using System.Collections.Generic;
using System.Linq;
using Entitas.SourceGenerator.CodeGeneration;
using Microsoft.CodeAnalysis;

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
    /// Single discovery entry point. Given a compilation (and the candidate types),
    /// runs the ported data providers and returns the full discovery data model.
    /// This is NOT yet wired into an IIncrementalGenerator (Phase 3); it is callable
    /// directly from tests.
    /// </summary>
    public static class EntitasDiscovery
    {
        public static DiscoveryResult Discover(Compilation compilation, bool ignoreNamespaces = false)
        {
            var resolver = ContextResolver.FromCompilation(compilation);
            var types = GetCandidateTypes(compilation).ToArray();
            return Discover(types, resolver, ignoreNamespaces);
        }

        public static DiscoveryResult Discover(INamedTypeSymbol[] types, ContextResolver resolver, bool ignoreNamespaces = false)
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
        /// Collects all named types declared in the compilation's source assembly.
        /// Phase 3 will narrow this via syntax predicates for incremental caching;
        /// for discovery/tests we walk the global namespace.
        /// </summary>
        public static IEnumerable<INamedTypeSymbol> GetCandidateTypes(Compilation compilation)
        {
            return GetAllTypes(compilation.Assembly.GlobalNamespace);
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
