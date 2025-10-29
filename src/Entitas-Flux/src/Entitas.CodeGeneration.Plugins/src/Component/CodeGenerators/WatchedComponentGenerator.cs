using System.IO;
using System.Linq;
using Jenny;

namespace Entitas.CodeGeneration.Plugins
{
	public class WatchedComponentGenerator : AbstractGenerator
	{
		public override string Name => "Component (Watch Changes)";

		const string TEMPLATE =
			@"[Entitas.CodeGeneration.Attributes.DontGenerate(false)]
public class ${ComponentName}Changed : Entitas.IComponent { }
";

		public override CodeGenFile[] Generate(CodeGeneratorData[] data) => data
			.OfType<ComponentData>()
			.Where(d => d.ShouldWatchChanges())
			.Select(GenerateSingle)
			.ToArray();

		CodeGenFile GenerateSingle(ComponentData data)
		{
			var fileContent = TEMPLATE.Replace("${ComponentName}", data.ComponentName());
			var fileName = (data.ComponentName() + "Changed").AddComponentSuffix();
			var path = "Components" + Path.DirectorySeparatorChar + fileName + ".cs";

			return new CodeGenFile(path, fileContent, GetType().FullName);
		}
	}
}
