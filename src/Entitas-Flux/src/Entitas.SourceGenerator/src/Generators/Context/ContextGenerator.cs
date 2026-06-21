using System.IO;
using System.Linq;

namespace Entitas.SourceGenerator.CodeGeneration
{
    public class ContextGenerator : AbstractGenerator
    {
        public override string Name => "Context";

        const string TEMPLATE =
            @"public sealed partial class ${ContextType} : Entitas.Context<${EntityType}> {

    public ${ContextType}()
        : base(
            ${Lookup}.TotalComponents,
            0,
            new Entitas.ContextInfo(
                ""${ContextName}"",
                ${Lookup}.componentNames,
                ${Lookup}.componentTypes
            ),
            (entity) =>

#if (ENTITAS_FAST_AND_UNSAFE)
                new Entitas.UnsafeAERC(),
#else
                new Entitas.SafeAERC(entity),
#endif
            () => new ${EntityType}()
        ) {
    }
}
";

        public override CodeGenFile[] Generate(CodeGeneratorData[] data) => data
            .OfType<ContextData>()
            .Select(generate)
            .ToArray();

        CodeGenFile generate(ContextData data)
        {
            var contextName = data.GetContextName();
            return new CodeGenFile(
                contextName + Path.DirectorySeparatorChar +
                contextName.AddContextSuffix() + ".cs",
                TEMPLATE.Replace(contextName),
                GetType().FullName
            );
        }
    }
}
