using System.Collections.Generic;
using System.IO;
using System.Linq;
using Jenny;
using Entitas.CodeGeneration.Plugins;

namespace Entitas.Roslyn.CodeGeneration.Plugins
{
	public sealed class WatchedCleanupSystemsGenerator : AbstractGenerator
	{
		public override string Name => "Watched Cleanup (Systems)";

		const string TEMPLATE =
			@"public sealed class ${ContextName}WatchedCleanupSystems : Feature {

    public ${ContextName}WatchedCleanupSystems(Contexts contexts) {
${systemsList}
    }
}
";

		public override CodeGenFile[] Generate(CodeGeneratorData[] data)
		{
			var watched = data
				.OfType<WatchedCleanupData>()
				.ToArray();

			var byContext = watched.Aggregate(
				new Dictionary<string, List<WatchedCleanupData>>(),
				(dict, d) =>
				{
					foreach (var ctx in d.componentData.GetContextNames())
					{
						if (!dict.ContainsKey(ctx))
							dict[ctx] = new List<WatchedCleanupData>();
						dict[ctx].Add(d);
					}
					return dict;
				});

			return byContext.Select(kv => Generate(kv.Key, kv.Value.ToArray())).ToArray();
		}

		CodeGenFile Generate(string contextName, WatchedCleanupData[] data)
		{
			var systemsList = string.Join("\n", data
				.Select(d => $"        Add(new Remove{d.componentData.ComponentName()}Changed{contextName.AddSystemSuffix()}(contexts));"));

			var fileContent = TEMPLATE
				.Replace("${systemsList}", systemsList)
				.Replace(contextName);

			return new CodeGenFile(
				contextName + Path.DirectorySeparatorChar +
				$"{contextName}WatchedCleanupSystems.cs",
				fileContent,
				GetType().FullName
			);
		}
	}
}
