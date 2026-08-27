using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Entitas.SourceGenerator.Analyzers;
using FluentAssertions;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CodeActions;
using Microsoft.CodeAnalysis.CodeFixes;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Text;
using Xunit;

namespace Entitas.CodeFixes.Tests
{
    /// <summary>
    /// Generation is opt-in, and an assembly that forgot to declare a context used to
    /// get silence: no generated API, no explanation. ENT0002 turns that into a warning
    /// on the component itself, with a one-click fix.
    /// </summary>
    public class MissingContextDefinitionTests
    {
        const string ComponentsWithoutContext = @"
public sealed class HealthComponent : Entitas.IComponent {
    public int value;
}

public sealed class DestroyedComponent : Entitas.IComponent { }

public abstract class AbstractComponent : Entitas.IComponent { }

public sealed class NotAComponent {
    public int value;
}
";

        const string ComponentsWithContext =
            "[assembly: Entitas.CodeGeneration.Attributes.ContextDefinition(\"Game\")]\n" + ComponentsWithoutContext;

        [Fact]
        public async Task Warns_on_every_component_when_no_context_is_declared()
        {
            var diagnostics = await AnalyzeAsync(ComponentsWithoutContext);

            diagnostics.Should().HaveCount(2, "abstract components and non-components are not reported");
            diagnostics.Select(d => d.GetMessage()).Should()
                .Contain(message => message.Contains("HealthComponent"))
                .And.Contain(message => message.Contains("DestroyedComponent"));

            var diagnostic = diagnostics[0];
            diagnostic.Id.Should().Be(MissingContextDefinitionAnalyzer.DiagnosticId);
            diagnostic.Severity.Should().Be(DiagnosticSeverity.Warning);
            diagnostic.Location.IsInSource.Should().BeTrue("the warning must point at the component");
        }

        [Fact]
        public async Task Says_nothing_when_a_context_is_declared()
        {
            (await AnalyzeAsync(ComponentsWithContext)).Should().BeEmpty();
        }

        [Fact]
        public async Task Says_nothing_when_the_assembly_does_not_use_entitas()
        {
            (await AnalyzeAsync("public sealed class Plain { public int value; }", withEntitas: false))
                .Should().BeEmpty();
        }

        [Fact]
        public async Task Points_at_the_right_assembly_when_the_contexts_live_elsewhere()
        {
            // Generation happens in the assembly that declares the contexts, so a
            // component anywhere else is silently ignored. Telling this user to declare a
            // context here would be wrong — it would generate a second, parallel set of
            // contexts — so it is a different diagnostic with no quick fix.
            var game = CSharpCompilation.Create(
                "Game.Core",
                new[] { CSharpSyntaxTree.ParseText("[assembly: Entitas.CodeGeneration.Attributes.ContextDefinition(\"Game\")]") },
                References(true),
                new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));

            var diagnostics = await AnalyzeAsync(CreateDocument(
                ComponentsWithoutContext,
                extraReferences: new[] { game.ToMetadataReference() }));

            diagnostics.Should().HaveCount(2);
            diagnostics.Should().OnlyContain(d =>
                d.Id == MissingContextDefinitionAnalyzer.ForeignAssemblyDiagnosticId);
            diagnostics[0].GetMessage().Should().Contain("Game.Core").And.Contain("move it");

            // The "declare a context here" fix must not be offered for this one.
            new MissingContextDefinitionCodeFixProvider().FixableDiagnosticIds
                .Should().NotContain(MissingContextDefinitionAnalyzer.ForeignAssemblyDiagnosticId);
        }

        [Fact]
        public async Task Fix_declares_a_context_and_clears_every_warning()
        {
            var document = CreateDocument(ComponentsWithoutContext);
            var diagnostics = await AnalyzeAsync(document);

            CodeAction? registered = null;
            await new MissingContextDefinitionCodeFixProvider().RegisterCodeFixesAsync(
                new CodeFixContext(document, diagnostics[0], (action, _) => registered = action, CancellationToken.None));

            registered.Should().NotBeNull();
            registered!.Title.Should().Contain("Game");

            var operations = await registered.GetOperationsAsync(CancellationToken.None);
            var changed = operations.OfType<ApplyChangesOperation>().Single().ChangedSolution;
            var fixedDocument = changed.GetDocument(document.Id)!;

            (await fixedDocument.GetTextAsync()).ToString().Should()
                .Contain("[assembly: Entitas.CodeGeneration.Attributes.ContextDefinition(\"Game\")]");

            // One attribute unblocks the whole assembly.
            (await AnalyzeAsync(fixedDocument)).Should().BeEmpty();
        }

        static async Task<Diagnostic[]> AnalyzeAsync(string source, bool withEntitas = true) =>
            await AnalyzeAsync(CreateDocument(source, withEntitas));

        static async Task<Diagnostic[]> AnalyzeAsync(Document document)
        {
            var compilation = await document.Project.GetCompilationAsync();
            var withAnalyzers = compilation!.WithAnalyzers(
                ImmutableArray.Create<DiagnosticAnalyzer>(new MissingContextDefinitionAnalyzer()));

            return (await withAnalyzers.GetAnalyzerDiagnosticsAsync())
                .OrderBy(d => d.Location.SourceSpan.Start)
                .ToArray();
        }

        static Document CreateDocument(
            string source,
            bool withEntitas = true,
            IEnumerable<MetadataReference>? extraReferences = null)
        {
            var projectId = ProjectId.CreateNewId();
            var documentId = DocumentId.CreateNewId(projectId);

            var solution = new AdhocWorkspace().CurrentSolution
                .AddProject(projectId, "TestAsm", "TestAsm", LanguageNames.CSharp)
                .WithProjectMetadataReferences(projectId, References(withEntitas).Concat(extraReferences ?? Enumerable.Empty<MetadataReference>()))
                .WithProjectCompilationOptions(projectId,
                    new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary))
                .AddDocument(documentId, "Components.cs", SourceText.From(source), filePath: "Components.cs");

            return solution.GetDocument(documentId)!;
        }

        static IEnumerable<MetadataReference> References(bool withEntitas)
        {
            var tpa = (string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!;
            foreach (var path in tpa.Split(Path.PathSeparator))
            {
                if (!string.IsNullOrEmpty(path) && File.Exists(path))
                    yield return MetadataReference.CreateFromFile(path);
            }

            if (!withEntitas)
                yield break;

            yield return MetadataReference.CreateFromFile(typeof(Entitas.IComponent).Assembly.Location);
            yield return MetadataReference.CreateFromFile(
                typeof(Entitas.CodeGeneration.Attributes.ContextAttribute).Assembly.Location);
        }
    }
}
