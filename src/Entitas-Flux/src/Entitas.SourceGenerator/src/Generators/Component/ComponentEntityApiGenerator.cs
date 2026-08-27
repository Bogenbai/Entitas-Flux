using System.IO;
using System.Linq;

namespace Entitas.SourceGenerator.CodeGeneration
{
    public class ComponentEntityApiGenerator : AbstractGenerator
    {
        public override string Name => "Component (Entity API)";

        const string HANDLE_WATCHED_CHANGES_KEY = "${Handle_Watched_Changes}";

        const string MARK_CHANGED_TEMPLATE = @"is${ComponentName}Changed = true;";

        const string STANDARD_TEMPLATE =
            @"public partial class ${EntityType} {

    public ${ComponentType} ${validComponentName} { get { return (${ComponentType})GetComponent(${Index}); } }
    public bool has${ComponentName} { get { return HasComponent(${Index}); } }

    public ${EntityType} Add${ComponentName}(${newMethodParameters}) {
        var index = ${Index};
        var componentPool = GetComponentPool(index);
        var component = componentPool.Count > 0
            ? (${ComponentType})componentPool.Pop()
            : new ${ComponentType}();
${memberAssignmentList}
        AddComponent(index, component);
        ${Handle_Watched_Changes}

        return this;
    }

    public ${EntityType} Replace${ComponentName}(${newMethodParameters}) {
        var index = ${Index};
        var componentPool = GetComponentPool(index);
        var component = componentPool.Count > 0
            ? (${ComponentType})componentPool.Pop()
            : new ${ComponentType}();
${memberAssignmentList}
        ReplaceComponent(index, component);
        ${Handle_Watched_Changes}

        return this;
    }

    public ${EntityType} Remove${ComponentName}() {
        RemoveComponent(${Index});
        ${Handle_Watched_Changes}

        return this;
    }


    public ${EntityType} SafeRemove${ComponentName}() {
        if (has${ComponentName})
        {
            RemoveComponent(${Index});
            ${Handle_Watched_Changes}
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
                    ${Handle_Watched_Changes}
                } else {
                    RemoveComponent(index);
                    ${Handle_Watched_Changes}
                }
            }
        }
    }
}
";

        public override CodeGenFile[] Generate(CodeGeneratorData[] data) => data
            .OfType<ComponentData>()
            .Where(d => d.ShouldGenerateMethods())
            .SelectMany(generate)
            .ToArray();

        CodeGenFile[] generate(ComponentData data) => data
            .GetContextNames()
            .Select(contextName => generate(contextName, data))
            .ToArray();

        CodeGenFile generate(string contextName, ComponentData data)
        {
            var template = data.GetMemberData().Length == 0
                ? FLAG_TEMPLATE
                : STANDARD_TEMPLATE;

            // [Watched] used to be wired into the atomic entity API only, so with the
            // default plain API the marker component and its cleanup systems were
            // generated and nothing ever set the flag.
            template = WatchedChanges.Apply(template, HANDLE_WATCHED_CHANGES_KEY,
                MARK_CHANGED_TEMPLATE, data.ShouldWatchChanges());

            var fileContent = template
                .Replace("${memberAssignmentList}", getMemberAssignmentList(data.GetMemberData()))
                .Replace(data, contextName);

            if (CodeGeneratorExtensions.debugHooks)
                fileContent = DebugHookInjector.Inject(fileContent, data, contextName);

            return new CodeGenFile(
                contextName + Path.DirectorySeparatorChar +
                "Components" + Path.DirectorySeparatorChar +
                data.ComponentNameWithContext(contextName).AddComponentSuffix() + ".cs",
                fileContent,
                GetType().FullName
            );
        }

        string getMemberAssignmentList(MemberData[] memberData) => string.Join("\n", memberData
            .Select(info => $"        component.{info.name} = new{info.name.ToUpperFirst()};"));
    }
}
