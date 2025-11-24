using System.IO;
using System.Linq;
using Jenny;

namespace Entitas.CodeGeneration.Plugins
{
	public class WatchedComponentGenerator : AbstractGenerator
	{
		public override string Name => "Component (Watch Changes)";

		const string TEMPLATE =
			@"[Entitas.CodeGeneration.Attributes.DontGenerate(false), ${Contexts}]
public class ${ComponentName}Changed : Entitas.IComponent { }
";

		public override CodeGenFile[] Generate(CodeGeneratorData[] data) => data
			.OfType<ComponentData>()
			.Where(d => d.ShouldWatchChanges())
			.Select(GenerateSingle)
			.ToArray();

		CodeGenFile GenerateSingle(ComponentData data)
		{
			string[] names = data.GetContextNames();
			string contexts = string.Join(", ", names);

			string fileContent = TEMPLATE
				.Replace("${ComponentName}", data.ComponentName())
				.Replace("${Contexts}", contexts);

			string fileName = (data.ComponentName() + "Changed").AddComponentSuffix();
			string path = "Components" + Path.DirectorySeparatorChar + fileName + ".cs";

			return new CodeGenFile(path, fileContent, GetType().FullName);
		}
	}
}
