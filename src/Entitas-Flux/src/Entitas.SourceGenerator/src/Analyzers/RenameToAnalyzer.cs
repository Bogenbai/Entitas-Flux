using System.Collections.Immutable;
using System.Linq;
using Entitas.SourceGenerator.CodeGeneration;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Diagnostics;

namespace Entitas.SourceGenerator.Analyzers
{
    /// <summary>
    /// Reports a pending [RenameTo("...")] on a component. The diagnostic is the hook
    /// the IDE quick fix (Entitas.CodeFixes) attaches to; it lives in this assembly
    /// because this is the DLL Unity already feeds to the compiler as an analyzer, and
    /// because reporting needs no Workspaces dependency.
    /// </summary>
    [DiagnosticAnalyzer(LanguageNames.CSharp)]
    public sealed class RenameToAnalyzer : DiagnosticAnalyzer
    {
        public const string DiagnosticId = "ENT0001";

        /// <summary>Diagnostic property carrying the requested new name to the code fix.</summary>
        public const string NewNameProperty = "NewName";

        static readonly DiagnosticDescriptor Rule = new DiagnosticDescriptor(
            DiagnosticId,
            "Component rename pending",
            "Component '{0}' is marked to be renamed to '{1}'. Apply the rename to update the generated API and its usages.",
            "Entitas",
            DiagnosticSeverity.Warning,
            isEnabledByDefault: true,
            description: "[RenameTo] does not change generation on its own — it records the intended new name " +
                         "so the IDE quick fix or entitas-rename can carry out the rename and remove the attribute.");

        public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics =>
            ImmutableArray.Create(Rule);

        public override void Initialize(AnalysisContext context)
        {
            context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
            context.EnableConcurrentExecution();
            context.RegisterSymbolAction(Analyze, SymbolKind.NamedType);
        }

        static void Analyze(SymbolAnalysisContext context)
        {
            var type = (INamedTypeSymbol)context.Symbol;

            var attribute = type.GetAttributes().FirstOrDefault(a =>
                a.AttributeClass?.ToDisplayString() == AttributeNames.RenameTo);
            if (attribute == null)
                return;

            var newName = attribute.ConstructorArguments.FirstOrDefault().Value as string;
            if (string.IsNullOrWhiteSpace(newName))
                return;

            var location = attribute.ApplicationSyntaxReference is { } reference
                ? Location.Create(reference.SyntaxTree, reference.Span)
                : type.Locations.FirstOrDefault();
            if (location == null)
                return;

            context.ReportDiagnostic(Diagnostic.Create(
                Rule,
                location,
                ImmutableDictionary<string, string?>.Empty.Add(NewNameProperty, newName),
                type.Name,
                newName));
        }
    }
}
