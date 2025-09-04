using System.IO;
using System.Linq;
using DesperateDevs.Extensions;
using Jenny;

namespace Entitas.CodeGeneration.Plugins
{
	public class AtomicComponentEntityApiGenerator : AbstractGenerator
	{
		public override string Name => "Component (Entity API)";

		const string VALUE_TYPE_KEY = "${ComponentValueType}";
		const string FIRST_UPPERCASE_COMPONENT_NAME_KEY = "${FirstUppercaseComponentName}";

		const string STANDARD_TEMPLATE =
			@"public partial class ${EntityType} {

    public ${ComponentType} ${validComponentName} { get { return (${ComponentType})GetComponent(${Index}); } }
    public bool has${ComponentName} { get { return HasComponent(${Index}); } }

    public ${EntityType} Add${ComponentName}(${newMethodParameters}) {
        var index = ${Index};
        var component = (${ComponentType})CreateComponent(index, typeof(${ComponentType}));
${memberAssignmentList}
        AddComponent(index, component);

        return this;
    }

    public ${EntityType} Remove${ComponentName}() {
        RemoveComponent(${Index});

        return this;
    }

    public ${EntityType} SafeRemove${ComponentName}() {
        if (has${ComponentName}) 
        {
            RemoveComponent(${Index});
        }

        return this;
    }
}
";

		const string ATOMIC_TEMPLATE =
			@"public partial class ${EntityType} {

    private ${ComponentType} ${validComponentName} { get { return (${ComponentType})GetComponent(${Index}); } }
    public ${ComponentValueType} ${FirstUppercaseComponentName} { get { return ${validComponentName}.Value; } }
    public bool has${ComponentName} { get { return HasComponent(${Index}); } }

    public ${EntityType} Add${ComponentName}(${newMethodParameters}) {
        var index = ${Index};
        var component = (${ComponentType})CreateComponent(index, typeof(${ComponentType}));
${memberAssignmentList}
        AddComponent(index, component);

        return this;
    }

    public ${EntityType} Replace${ComponentName}(${newMethodParameters}) {
        if (!has${ComponentName}) 
        {
            Add${ComponentName}(newValue);
            return this;
        }

        ${validComponentName}.Value = newValue;
        return this;
    }

    public ${EntityType} Remove${ComponentName}() {
        RemoveComponent(${Index});
        return this;
    }

    public ${EntityType} SafeRemove${ComponentName}() {
        if (has${ComponentName}) 
        {
            RemoveComponent(${Index});
        }

        return this;
    }
}
";

		const string FLAG_TEMPLATE =
			@"public partial class ${EntityType} {

    static readonly ${ComponentType} ${componentName}Component = new ${ComponentType}();

    public bool ${prefixedComponentName} {
        get { return HasComponent(${Index}); }
        set {
            if (value != ${prefixedComponentName}) {
                var index = ${Index};
                if (value) {
                    var componentPool = GetComponentPool(index);
                    var component = componentPool.Count > 0
                            ? componentPool.Pop()
                            : ${componentName}Component;

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
			.SelectMany(Generate)
			.ToArray();

		CodeGenFile[] Generate(ComponentData data) => data
			.GetContextNames()
			.Select(contextName => Generate(contextName, data))
			.ToArray();

		CodeGenFile Generate(string contextName, ComponentData data)
		{
			string template;

			MemberData[] members = data.GetMemberData();
			if (members.IsAtomicComponent())
				template = ATOMIC_TEMPLATE;
			else if (data.GetMemberData().Length == 0)
				template = FLAG_TEMPLATE;
			else
				template = STANDARD_TEMPLATE;

			var fileContent = template
				.Replace("${memberAssignmentList}", GetMemberAssignmentList(data.GetMemberData()))
				.Replace(data, contextName);

			if (members.IsAtomicComponent())
			{
				MemberData member = members.First();

				fileContent = fileContent
					.Replace(VALUE_TYPE_KEY, member.type)
					.Replace(FIRST_UPPERCASE_COMPONENT_NAME_KEY, data.ComponentName().ToUpperFirst());
			}

			return new CodeGenFile(
				contextName + Path.DirectorySeparatorChar +
				"Components" + Path.DirectorySeparatorChar +
				data.ComponentNameWithContext(contextName).AddComponentSuffix() + ".cs",
				fileContent,
				GetType().FullName
			);
		}

		string GetMemberAssignmentList(MemberData[] memberData) => string.Join("\n", memberData
			.Select(info => $"        component.{info.name} = new{info.name.ToUpperFirst()};"));
	}
}
