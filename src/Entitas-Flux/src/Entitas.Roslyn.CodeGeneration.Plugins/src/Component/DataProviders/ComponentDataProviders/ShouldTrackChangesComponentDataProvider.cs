using DesperateDevs.Roslyn;
using Entitas.CodeGeneration.Attributes;
using Entitas.CodeGeneration.Plugins;
using Microsoft.CodeAnalysis;

namespace Entitas.Roslyn.CodeGeneration.Plugins
{
	public class ShouldTrackChangesComponentDataProvider : IComponentDataProvider
	{
		public void Provide(INamedTypeSymbol type, ComponentData data)
		{
			var shouldTrackChanges = type.GetAttribute<TrackChangesAttribute>() != null;
			data.ShouldTrackChanges(shouldTrackChanges);
		}
	}
}
