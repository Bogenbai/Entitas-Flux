using System;
using System.Linq;
using Entitas.CodeGeneration.Attributes;

namespace Entitas.CodeGeneration.Plugins
{
	public class ShouldWatchComponentDataProvider : IComponentDataProvider
	{
		public void Provide(Type type, ComponentData data)
		{
			var shouldTrackChanges = Attribute
				.GetCustomAttributes(type)
				.OfType<WatchedAttribute>()
				.Any();

			data.ShouldWatchChanges(shouldTrackChanges);
		}
	}

	public static class WatchChangesComponentDataExtension
	{
		public const string COMPONENT_WATCHED = "Component.Watched";
		public static bool ShouldWatchChanges(this ComponentData data) => (bool)data[COMPONENT_WATCHED];
		public static void ShouldWatchChanges(this ComponentData data, bool isTrackingChanges) => data[COMPONENT_WATCHED] = isTrackingChanges;
	}
}
