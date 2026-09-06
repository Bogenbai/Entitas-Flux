using System.Linq;

namespace Entitas.SourceGenerator.CodeGeneration
{
    public class ContextObserverSingleObjectGenerator : AbstractGenerator
    {
        public override string Name => "Context Observer (Single GameObject)";

        const string CONTEXTS_TEMPLATE =
            @"public partial class Contexts {

#if (!ENTITAS_DISABLE_VISUAL_DEBUGGING && UNITY_EDITOR)

    [Entitas.CodeGeneration.Attributes.PostConstructor]
    public void InitializeContextObservers() {
        try {
${contextObservers}
        } catch(System.Exception e) {
            UnityEngine.Debug.LogError(e);
        }
    }

    public void CreateContextObserver(Entitas.IContext context) {
        if (UnityEngine.Application.isPlaying) {
            Entitas.VisualDebugging.Unity.EntitasDebugBehaviour.Observe(context);
        }
    }

#endif
}
";

        const string CONTEXT_OBSERVER_TEMPLATE = @"            CreateContextObserver(${contextName});";

        public override CodeGenFile[] Generate(CodeGeneratorData[] data)
        {
            var contextNames = data
                .OfType<ContextData>()
                .Select(d => d.GetContextName())
                .OrderBy(contextName => contextName)
                .ToArray();

            return new[]
            {
                new CodeGenFile(
                    "Contexts.cs",
                    generateContextsClass(contextNames),
                    GetType().FullName)
            };
        }

        string generateContextsClass(string[] contextNames)
        {
            var contextObservers = string.Join("\n", contextNames
                .Select(contextName => CONTEXT_OBSERVER_TEMPLATE
                    .Replace("${contextName}", contextName.ToLowerFirst())));

            return CONTEXTS_TEMPLATE
                .Replace("${contextObservers}", contextObservers);
        }
    }
}
