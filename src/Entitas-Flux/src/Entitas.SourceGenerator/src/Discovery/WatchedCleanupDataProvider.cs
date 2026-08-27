using System.Linq;
using Entitas.SourceGenerator.CodeGeneration;

namespace Entitas.SourceGenerator.Discovery
{
    /// <summary>
    /// Ported from the Roslyn WatchedCleanupDataProvider. Reuses ComponentDataProvider
    /// on the filtered set of [Watched] component types, then wraps the still-watched
    /// component data in WatchedCleanupData.
    /// </summary>
    public sealed class WatchedCleanupDataProvider
    {
        readonly TypeSnapshot[] _types;
        readonly ContextResolver _contextResolver;
        readonly bool _ignoreNamespaces;

        public WatchedCleanupDataProvider(TypeSnapshot[] types, ContextResolver contextResolver, bool ignoreNamespaces = false)
        {
            _types = types;
            _contextResolver = contextResolver;
            _ignoreNamespaces = ignoreNamespaces;
        }

        public WatchedCleanupData[] GetData()
        {
            var watchedTypes = _types
                .Where(t => t.IsComponent)
                .Where(t => !t.IsAbstract)
                .Where(t => t.GetAttribute(AttributeNames.Watched) != null)
                .ToArray();

            var componentDataProvider = new ComponentDataProvider(watchedTypes, _contextResolver, _ignoreNamespaces);

            return componentDataProvider
                .GetData()
                .Where(d => !d.GetTypeName().RemoveComponentSuffix().HasListenerSuffix())
                .Where(d => d.ShouldWatchChanges())
                .Select(d => new WatchedCleanupData(d))
                .ToArray();
        }
    }
}
