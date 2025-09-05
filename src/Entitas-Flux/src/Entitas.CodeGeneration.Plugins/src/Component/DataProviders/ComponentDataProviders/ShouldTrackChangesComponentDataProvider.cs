using System;
using System.Linq;
using Entitas.CodeGeneration.Attributes;

namespace Entitas.CodeGeneration.Plugins
{
	public class ShouldTrackChangesComponentDataProvider : IComponentDataProvider
	{
		public void Provide(Type type, ComponentData data)
		{
			var shouldTrackChanges = Attribute
				.GetCustomAttributes(type)
				.OfType<TrackChangesAttribute>()
				.Any();

			data.ShouldTrackChanges(shouldTrackChanges);
		}
	}

	public static class TrackChangesComponentDataExtension
	{
		public const string COMPONENT_TRACK_CHANGES = "Component.ShouldTrackChanges";
		public static bool ShouldTrackChanges(this ComponentData data) => (bool)data[COMPONENT_TRACK_CHANGES];
		public static void ShouldTrackChanges(this ComponentData data, bool isTrackingChanges) => data[COMPONENT_TRACK_CHANGES] = isTrackingChanges;
	}
}
