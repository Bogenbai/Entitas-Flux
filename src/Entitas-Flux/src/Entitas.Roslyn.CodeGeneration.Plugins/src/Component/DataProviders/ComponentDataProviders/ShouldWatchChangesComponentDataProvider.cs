using DesperateDevs.Roslyn;
using Entitas.CodeGeneration.Attributes;
using Entitas.CodeGeneration.Plugins;
using Microsoft.CodeAnalysis;

namespace Entitas.Roslyn.CodeGeneration.Plugins
{
	public class ShouldWatchChangesComponentDataProvider : IComponentDataProvider
	{
		public void Provide(INamedTypeSymbol type, ComponentData data)
		{
			var shouldTrackChanges = type.GetAttribute<WatchedAttribute>() != null;
			data.ShouldWatchChanges(shouldTrackChanges);
		}
	}
}
