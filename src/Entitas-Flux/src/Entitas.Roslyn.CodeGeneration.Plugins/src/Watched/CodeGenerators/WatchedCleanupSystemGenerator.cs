using System.IO;
using System.Linq;
using Jenny;
using Entitas.CodeGeneration.Plugins;

namespace Entitas.Roslyn.CodeGeneration.Plugins
{
    public sealed class WatchedCleanupSystemGenerator : AbstractGenerator
    {
        public override string Name => "Watched Cleanup (System)";

        const string TEMPLATE =
@"using System.Collections.Generic;
using Entitas;

public sealed class Remove${ComponentName}Changed${SystemType} : ICleanupSystem {

    readonly IGroup<${EntityType}> _group;
    readonly List<${EntityType}> _buffer = new List<${EntityType}>(8);

    public Remove${ComponentName}Changed${SystemType}(Contexts contexts) {
        _group = contexts.${contextName}.GetGroup(${MatcherType}.${ComponentName}Changed);
    }

    public void Cleanup() {
        foreach (var e in _group.GetEntities(_buffer)) {
            e.is${ComponentName}Changed = false;
        }
    }
}
";

        public override CodeGenFile[] Generate(CodeGeneratorData[] data) => data
            .OfType<WatchedCleanupData>()
            .SelectMany(Generate)
            .ToArray();

        CodeGenFile[] Generate(WatchedCleanupData data) => data
            .componentData
            .GetContextNames()
            .Select(ctx => Generate(ctx, data.componentData))
            .ToArray();

        CodeGenFile Generate(string contextName, ComponentData data)
        {
            var fileContent = TEMPLATE
                .Replace("${SystemType}", contextName.AddSystemSuffix())
                .Replace("${EntityType}", contextName.AddEntitySuffix())
                .Replace("${MatcherType}", contextName.AddMatcherSuffix())
                .Replace(data, contextName)
                .Replace("${ComponentName}", data.ComponentName());

            var fileName = $"Remove{data.ComponentName()}Changed{contextName.AddSystemSuffix()}.cs";

            return new CodeGenFile(
                contextName + Path.DirectorySeparatorChar +
                "Systems" + Path.DirectorySeparatorChar +
                fileName,
                fileContent,
                GetType().FullName
            );
        }
    }
}
