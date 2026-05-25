using System.IO;
using System.Linq;
using Jenny;

namespace Entitas.CodeGeneration.Plugins
{
    public class ComponentEntityApiInterfaceGenerator : AbstractGenerator
    {
        public override string Name => "Component (Entity API Interface)";

        const string STANDARD_TEMPLATE =
            @"public partial interface I${ComponentName}Entity {

    ${ComponentType} ${validComponentName} { get; }
    bool has${ComponentName} { get; }

    void Add${ComponentName}(${newMethodParameters});
    void Replace${ComponentName}(${newMethodParameters});
    void Remove${ComponentName}();
}
";

        const string FLAG_TEMPLATE =
            @"public partial interface I${ComponentName}Entity {
    bool ${prefixedComponentName} { get; set; }
}
";

        // Flag components are satisfied implicitly by the generated property.
        const string ENTITY_INTERFACE_TEMPLATE = "public partial class ${EntityType} : I${ComponentName}Entity { }\n";

        // Standard/atomic components expose fluent Add/Replace/Remove that return
        // ${EntityType}. The shared interface (used across multiple contexts) must
        // declare them as void, so we forward via explicit interface implementations.
        // The public fluent methods stay intact; only calls made through the
        // interface return void.
        const string ENTITY_INTERFACE_STANDARD_TEMPLATE =
            @"public partial class ${EntityType} : I${ComponentName}Entity {
    void I${ComponentName}Entity.Add${ComponentName}(${newMethodParameters}) { Add${ComponentName}(${newMethodArgs}); }
    void I${ComponentName}Entity.Replace${ComponentName}(${newMethodParameters}) { Replace${ComponentName}(${newMethodArgs}); }
    void I${ComponentName}Entity.Remove${ComponentName}() { Remove${ComponentName}(); }
}
";

        public override CodeGenFile[] Generate(CodeGeneratorData[] data) => data
            .OfType<ComponentData>()
            .Where(d => d.ShouldGenerateMethods())
            .Where(d => d.GetContextNames().Length > 1)
            .SelectMany(generate)
            .ToArray();

        CodeGenFile[] generate(ComponentData data) => new[] {generateInterface(data)}
            .Concat(data.GetContextNames().Select(contextName => generateEntityInterface(contextName, data)))
            .ToArray();

        CodeGenFile generateInterface(ComponentData data)
        {
            var template = data.GetMemberData().Length == 0
                ? FLAG_TEMPLATE
                : STANDARD_TEMPLATE;

            return new CodeGenFile(
                "Components" + Path.DirectorySeparatorChar +
                "Interfaces" + Path.DirectorySeparatorChar +
                "I" + data.ComponentName() + "Entity.cs",
                template.Replace(data, string.Empty),
                GetType().FullName
            );
        }

        CodeGenFile generateEntityInterface(string contextName, ComponentData data)
        {
            var template = data.GetMemberData().Length == 0
                ? ENTITY_INTERFACE_TEMPLATE
                : ENTITY_INTERFACE_STANDARD_TEMPLATE;

            return new CodeGenFile(
                contextName + Path.DirectorySeparatorChar +
                "Components" + Path.DirectorySeparatorChar +
                data.ComponentNameWithContext(contextName).AddComponentSuffix() + ".cs",
                template.Replace(data, contextName),
                GetType().FullName
            );
        }
    }
}
