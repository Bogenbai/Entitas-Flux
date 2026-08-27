using System.Collections.Immutable;
using System.Linq;
using Entitas.SourceGenerator.CodeGeneration;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Diagnostics;

namespace Entitas.SourceGenerator.Analyzers
{
    /// <summary>
    /// Reports components that no code will be generated for.
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

        /// <summary>The same silence, but with a different cause and a different fix.</summary>
        public const string ForeignAssemblyDiagnosticId = "ENT0003";

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

        /// <summary>
        /// Generation happens per assembly, in the one that declares the contexts, so a
        /// component in a different assembly is simply ignored — silently, which is how
        /// an afternoon disappears. Splitting components across assemblies needs the
        /// generated entity API to cross an assembly boundary, which C# partial classes
        /// cannot do; until that is solved, components belong with their contexts.
        /// </summary>
        static readonly DiagnosticDescriptor ForeignAssemblyRule = new DiagnosticDescriptor(
            ForeignAssemblyDiagnosticId,
            "Component is in a different assembly than its contexts",
            "'{0}' is a component, but Entitas generates code only in the assembly that declares the contexts — " +
            "'{1}' declares them, this one does not. No code is generated for '{0}': move it to '{1}'.",
            "Entitas",
            DiagnosticSeverity.Warning,
            isEnabledByDefault: true,
            description: "Entitas-Flux generates the ECS API per assembly, into the assembly that declares its " +
                         "contexts. Components in another assembly are not discovered. Declaring contexts here as " +
                         "well would not help: it would generate a second, parallel set of contexts and entities.");

        public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics =>
            ImmutableArray.Create(Rule, ForeignAssemblyRule);

        public override void Initialize(AnalysisContext context)
        {
            context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
            context.EnableConcurrentExecution();

            context.RegisterCompilationStartAction(start =>
            {
                var componentInterface = start.Compilation.GetTypeByMetadataName(WellKnownTypes.ComponentInterface);
                if (componentInterface is null)
                    return; // The assembly does not use Entitas at all.

                if (DeclaresContexts(start.Compilation.Assembly))
                    return; // Configured; nothing to say.

                // Contexts declared somewhere we reference means the components here are
                // in the wrong assembly, not that a context is missing — a different
                // problem with a different fix.
                var contextAssembly = start.Compilation.SourceModule.ReferencedAssemblySymbols
                    .FirstOrDefault(DeclaresContexts);

                start.RegisterSymbolAction(
                    symbolContext => Analyze(symbolContext, componentInterface, contextAssembly),
                    SymbolKind.NamedType);
            });
        }

        static bool DeclaresContexts(IAssemblySymbol assembly) => assembly.GetAttributes().Any(attribute =>
            attribute.AttributeClass?.ToDisplayString() == AttributeNames.ContextDefinition);

        static void Analyze(
            SymbolAnalysisContext context,
            INamedTypeSymbol componentInterface,
            IAssemblySymbol? contextAssembly)
        {
            var type = (INamedTypeSymbol)context.Symbol;
            if (type.IsAbstract)
                return;

            if (!type.AllInterfaces.Any(i => SymbolEqualityComparer.Default.Equals(i, componentInterface)))
                return;

            var location = type.Locations.FirstOrDefault(l => l.IsInSource);
            if (location == null)
                return;

            context.ReportDiagnostic(contextAssembly == null
                ? Diagnostic.Create(Rule, location, type.Name)
                : Diagnostic.Create(ForeignAssemblyRule, location, type.Name, contextAssembly.Name));
        }
    }
}
