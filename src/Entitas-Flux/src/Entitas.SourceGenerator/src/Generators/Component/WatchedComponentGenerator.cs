using System.IO;
using System.Linq;

namespace Entitas.SourceGenerator.CodeGeneration
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
			.GroupBy(d => d.ComponentName())
			.Select(GenerateForGroup)
			.ToArray();

		CodeGenFile GenerateForGroup(IGrouping<string, ComponentData> group)
		{
			string componentName = group.Key;
			string[] allContexts = group
				.SelectMany(d => d.GetContextNames())
				.Distinct()
				.OrderBy(ctx => ctx)
				.ToArray();
			string contexts = string.Join(", ", allContexts);

			string fileContent = TEMPLATE
				.Replace("${ComponentName}", componentName)
				.Replace("${Contexts}", contexts);

			string fileName = (componentName + "Changed").AddComponentSuffix();
			string path = "Components" + Path.DirectorySeparatorChar + fileName + ".cs";

			return new CodeGenFile(path, fileContent, GetType().FullName);
		}
	}
}
