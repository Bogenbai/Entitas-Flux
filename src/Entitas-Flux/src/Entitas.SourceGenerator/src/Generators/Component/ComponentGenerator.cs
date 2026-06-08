using System.IO;
using System.Linq;

namespace Entitas.SourceGenerator.CodeGeneration
{
    public class ComponentGenerator : AbstractGenerator
    {
        public override string Name => "Component";

        const string COMPONENT_TEMPLATE =
            @"[Entitas.CodeGeneration.Attributes.DontGenerate(false)]
public sealed class ${FullComponentName} : Entitas.IComponent {
    public ${Type} value;
}
";

        public override CodeGenFile[] Generate(CodeGeneratorData[] data) => data
            .OfType<ComponentData>()
            .Where(d => d.ShouldGenerateComponent())
            .Select(generate)
            .ToArray();

        CodeGenFile generate(ComponentData data)
        {
            var fullComponentName = data.GetTypeName().RemoveDots();
            return new CodeGenFile(
                "Components" + Path.DirectorySeparatorChar +
                fullComponentName + ".cs",
                COMPONENT_TEMPLATE
                    .Replace("${FullComponentName}", fullComponentName)
                    .Replace("${Type}", data.GetObjectTypeName()),
                GetType().FullName
            );
        }
    }
}
