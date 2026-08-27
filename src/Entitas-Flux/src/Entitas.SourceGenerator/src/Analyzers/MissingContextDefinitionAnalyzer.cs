using System.Collections.Immutable;
using System.Linq;
using Entitas.SourceGenerator.CodeGeneration;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Diagnostics;

namespace Entitas.SourceGenerator.Analyzers
{
    /// <summary>
    /// Reports components in an assembly that declares no context.
    ///
    /// Generation is opt-in: without an [assembly: ContextDefinition("…")] the generator
    /// emits nothing at all. That silence is the single most confusing way to meet this
    /// framework — you write a component, GameEntity does not exist, and nothing anywhere
    /// says why. The condition is narrow on purpose: an assembly that merely CONSUMES the
    /// generated API (a systems asmdef, a test assembly) declares no components and is
    /// left alone, and an assembly that does not reference Entitas is never looked at.
    /// </summary>
    [DiagnosticAnalyzer(LanguageNames.CSharp)]
    public sealed class MissingContextDefinitionAnalyzer : DiagnosticAnalyzer
    {
        public const string DiagnosticId = "ENT0002";

        static readonly DiagnosticDescriptor Rule = new DiagnosticDescriptor(
            DiagnosticId,
            "No context declared for this assembly",
            "'{0}' is a component, but this assembly declares no context, so Entitas generates no code for it. " +
            "Add [assembly: ContextDefinition(\"Game\")] (any file will do).",
            "Entitas",
            DiagnosticSeverity.Warning,
            isEnabledByDefault: true,
            description: "Entitas-Flux generates the ECS API only for assemblies that declare at least one " +
                         "context with [assembly: ContextDefinition(\"…\")]. The first one declared is the " +
                         "default context for components without an explicit context attribute.");

        public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics =>
            ImmutableArray.Create(Rule);

        public override void Initialize(AnalysisContext context)
        {
            context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
            context.EnableConcurrentExecution();

            context.RegisterCompilationStartAction(start =>
            {
                var componentInterface = start.Compilation.GetTypeByMetadataName(WellKnownTypes.ComponentInterface);
                if (componentInterface is null)
                    return; // The assembly does not use Entitas at all.

                var hasContexts = start.Compilation.Assembly.GetAttributes().Any(attribute =>
                    attribute.AttributeClass?.ToDisplayString() == AttributeNames.ContextDefinition);
                if (hasContexts)
                    return; // Configured; nothing to say.

                start.RegisterSymbolAction(symbolContext => Analyze(symbolContext, componentInterface), SymbolKind.NamedType);
            });
        }

        static void Analyze(SymbolAnalysisContext context, INamedTypeSymbol componentInterface)
        {
            var type = (INamedTypeSymbol)context.Symbol;
            if (type.IsAbstract)
                return;

            if (!type.AllInterfaces.Any(i => SymbolEqualityComparer.Default.Equals(i, componentInterface)))
                return;

            var location = type.Locations.FirstOrDefault(l => l.IsInSource);
            if (location == null)
                return;

            context.ReportDiagnostic(Diagnostic.Create(Rule, location, type.Name));
        }
    }
}
