using System;
using System.Linq;
using Entitas.CodeGeneration.Attributes;

namespace Entitas.CodeGeneration.Plugins
{
	public class ShouldTrackChangesComponentDataProvider : IComponentDataProvider
	{
		public void Provide(Type type, ComponentData data)
		{
			var attrs = Attribute.GetCustomAttributes(type)
				.OfType<TrackChangesAttribute>()
				.ToArray();

			if (attrs.Length > 0)
			{
				data.ShouldTrackChanges(true);
			}
			else
			{
				data.ShouldTrackChanges(false);
			}
		}
	}

	public static class TrackChangesComponentDataExtension
	{
		public const string COMPONENT_TRACK_CHANGES = "Component.ShouldTrackChanges";
		public static bool ShouldTrackingChanges(this ComponentData data) => (bool)data[COMPONENT_TRACK_CHANGES];
		public static void ShouldTrackChanges(this ComponentData data, bool isTrackingChanges) => data[COMPONENT_TRACK_CHANGES] = isTrackingChanges;
	}
}
