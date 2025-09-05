using Jenny;
using Entitas.CodeGeneration.Plugins;

namespace Entitas.Roslyn.CodeGeneration.Plugins
{
	public sealed class WatchedCleanupData : CodeGeneratorData
	{
		public ComponentData componentData => _componentData;

		readonly ComponentData _componentData;

		public WatchedCleanupData(CodeGeneratorData data) : base(data)
		{
			_componentData = (ComponentData)data;
		}
	}
}
