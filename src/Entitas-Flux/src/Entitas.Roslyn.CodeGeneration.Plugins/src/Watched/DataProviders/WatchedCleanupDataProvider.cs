using System.Linq;
using System.Collections.Generic;
using DesperateDevs.Extensions;
using Jenny;
using Jenny.Plugins;
using DesperateDevs.Serialization;
using DesperateDevs.Roslyn;
using Entitas.CodeGeneration.Attributes;
using Entitas.CodeGeneration.Plugins;
using Microsoft.CodeAnalysis;

namespace Entitas.Roslyn.CodeGeneration.Plugins
{
    public sealed class WatchedCleanupDataProvider : IDataProvider, IConfigurable, ICachable
    {
        public string Name => "Watched Cleanup";
        public int Order => 0;
        public bool RunInDryMode => true;

        public Dictionary<string, string> DefaultProperties => _projectPathConfig.DefaultProperties;
        public Dictionary<string, object> ObjectCache { get; set; }

        readonly ProjectPathConfig _projectPathConfig = new ProjectPathConfig();
        readonly INamedTypeSymbol[] _types;

        Preferences _preferences;
        ComponentDataProvider _componentDataProvider;

        public WatchedCleanupDataProvider() : this(null) { }
        public WatchedCleanupDataProvider(INamedTypeSymbol[] types) { _types = types; }

        public void Configure(Preferences preferences)
        {
            _preferences = preferences;
            _projectPathConfig.Configure(preferences);
        }

        public CodeGeneratorData[] GetData()
        {
            var types = _types ?? Jenny.Plugins.Roslyn.PluginUtil
                .GetCachedProjectParser(ObjectCache, _projectPathConfig.ProjectPath)
                .GetTypes();

            var componentInterface = typeof(IComponent).ToCompilableString();
            var watchedTypes = types
                .Where(t => t.AllInterfaces.Any(i => i.ToCompilableString() == componentInterface))
                .Where(t => !t.IsAbstract)
                .Where(t => t.GetAttribute<WatchedAttribute>() != null)
                .ToArray();

            _componentDataProvider = new ComponentDataProvider(watchedTypes);
            _componentDataProvider.Configure(_preferences);

            return _componentDataProvider
                .GetData()
                .OfType<ComponentData>()
                .Where(d => !d.GetTypeName().RemoveComponentSuffix().HasListenerSuffix())
                .Where(d => d.ShouldWatchChanges())
                .Select(d => new WatchedCleanupData(d))
                .ToArray();
        }
    }
}
