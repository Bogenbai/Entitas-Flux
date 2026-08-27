using System.Linq;
using Entitas.SourceGenerator.CodeGeneration;

namespace Entitas.SourceGenerator.Discovery
{
    /// <summary>
    /// Ported from the Roslyn CleanupDataProvider. Reuses ComponentDataProvider on
    /// the filtered set of [Cleanup] component types, then wraps the resulting
    /// component data in CleanupData carrying the cleanup mode.
    /// </summary>
    public sealed class CleanupDataProvider
    {
        readonly TypeSnapshot[] _types;
        readonly ContextResolver _contextResolver;
        readonly bool _ignoreNamespaces;

        public CleanupDataProvider(TypeSnapshot[] types, ContextResolver contextResolver, bool ignoreNamespaces = false)
        {
            _types = types;
            _contextResolver = contextResolver;
            _ignoreNamespaces = ignoreNamespaces;
        }

        public CleanupData[] GetData()
        {
            var cleanupTypes = _types
                .Where(type => type.IsComponent)
                .Where(type => !type.IsAbstract)
                .Where(type => type.GetAttribute(AttributeNames.Cleanup) != null)
                .ToArray();

            var cleanupLookup = cleanupTypes.ToDictionary(
                type => type.FullName,
                type => (CleanupMode)int.Parse(type.GetAttribute(AttributeNames.Cleanup)!.Arguments[0]!));

            var componentDataProvider = new ComponentDataProvider(cleanupTypes, _contextResolver, _ignoreNamespaces);

            return componentDataProvider
                .GetData()
                .Where(data => cleanupLookup.ContainsKey(data.GetTypeName()))
                .Where(data => !data.GetTypeName().RemoveComponentSuffix().HasListenerSuffix())
                .Select(data => new CleanupData(data) { cleanupMode = cleanupLookup[data.GetTypeName()] })
                .ToArray();
        }
    }
}
