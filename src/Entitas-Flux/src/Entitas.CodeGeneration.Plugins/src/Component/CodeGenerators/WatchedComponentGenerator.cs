using System.IO;
using System.Linq;
using Jenny;

namespace Entitas.CodeGeneration.Plugins
{
	public class WatchedComponentGenerator : AbstractGenerator
	{
		public override string Name => "Component (Watch Changes)";
		
		const string CHANGED_FLAG_TEMPLATE =
			@"[Entitas.CodeGeneration.Attributes.DontGenerate(false)]
public class ${ComponentName}Changed : Entitas.IComponent { }

public partial class ${EntityType} {

    static readonly ${ComponentName}Changed ${componentName}ChangedComponent = new ${ComponentName}Changed();

    public bool ${prefixedComponentName}Changed {
        get { return HasComponent(${Index}); }
        set {
            if (value != ${prefixedComponentName}Changed) {
                var index = ${Index};
                if (value) {
                    var componentPool = GetComponentPool(index);
                    var component = componentPool.Count > 0
                            ? componentPool.Pop()
                            : ${componentName}ChangedComponent;

                    AddComponent(index, component);
                } else {
                    RemoveComponent(index);
                }
            }
        }
    }
}
";
		
		public override CodeGenFile[] Generate(CodeGeneratorData[] data) => data
			.OfType<ComponentData>()
			.Where(d => d.ShouldGenerateMethods())
			.Where(d => d.ShouldWatchChanges())
			.SelectMany(Generate)
			.ToArray();

		CodeGenFile[] Generate(ComponentData data) => data
			.GetContextNames()
			.Select(contextName => Generate(contextName, data))
			.ToArray();
		
		CodeGenFile Generate(string contextName, ComponentData data)
		{
			string template = CHANGED_FLAG_TEMPLATE;

			var fileContent = template
				.Replace(data, contextName);

			return new CodeGenFile(
				contextName + Path.DirectorySeparatorChar +
				"Components" + Path.DirectorySeparatorChar +
				data.ComponentNameWithContext(contextName).AddComponentSuffix() + ".cs",
				fileContent,
				GetType().FullName
			);
		}
	}
}
