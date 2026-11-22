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
    public class RenameToCodeFixTests
    {
        const string ComponentSource = @"using Entitas.CodeGeneration.Attributes;

[assembly: ContextDefinition(""Game"")]

[RenameTo(""Hp"")]
public sealed class HealthComponent : Entitas.IComponent {
    public int value;
}
";

        const string SystemSource = @"public sealed class DamageSystem {
    public void Execute(GameEntity entity) {
        if (entity.hasHealth)
            entity.ReplaceHealth(entity.health.value - 1);
        else
            entity.AddHealth(10);

        entity.SafeRemoveHealth();
        var matcher = GameMatcher.Health;
    }
}
";

        const string UnrelatedSource = @"public sealed class Monster {
    public int Health;
    public int Read() => Health;
}
";

        [Fact]
        public async Task Reports_a_pending_rename()
        {
            var (_, diagnostics) = await AnalyzeAsync();

            diagnostics.Should().HaveCount(1);
            diagnostics[0].Id.Should().Be(RenameToAnalyzer.DiagnosticId);
            diagnostics[0].GetMessage().Should().Contain("HealthComponent").And.Contain("Hp");
            diagnostics[0].Properties[RenameToAnalyzer.NewNameProperty].Should().Be("Hp");
        }

        [Fact]
        public async Task Fix_renames_the_generated_api_across_documents_and_drops_the_attribute()
        {
            var texts = await ApplyFixAsync();

            texts["Components.cs"].Should()
                .Contain("class HpComponent")
                .And.NotContain("RenameTo")
                .And.NotContain("HealthComponent");

            // The whole point: the generated API in OTHER documents follows.
            texts["DamageSystem.cs"].Should()
                .Contain("entity.hasHp")
                .And.Contain("entity.ReplaceHp(entity.hp.value - 1)")
                .And.Contain("entity.AddHp(10)")
                .And.Contain("entity.SafeRemoveHp()")
                .And.Contain("GameMatcher.Hp")
                .And.NotContain("Health");
        }

        [Fact]
        public async Task Fix_leaves_unrelated_members_alone()
        {
            var texts = await ApplyFixAsync();

            texts["Unrelated.cs"].Should().Be(UnrelatedSource);
        }

        /// <summary>
        /// Output = GenerationOutput.Files: the generated API is in ordinary documents. The
        /// fix must resolve usages against them (not add a second copy, which would make
        /// every member ambiguous) and rewrite them too, so the project compiles straight
        /// after the fix — regeneration then only moves the files to their new names.
        /// </summary>
        [Fact]
        public async Task Fix_works_when_the_generated_code_is_in_files()
        {
            var (solution, _) = await ApplyFixToSolutionAsync(filesMode: true);
            var project = solution.Projects.Single();

            var texts = new Dictionary<string, string>();
            foreach (var doc in project.Documents)
                texts[doc.Name] = (await doc.GetTextAsync()).ToString();

            texts["DamageSystem.cs"].Should()
                .Contain("entity.ReplaceHp(entity.hp.value - 1)")
                .And.Contain("GameMatcher.Hp")
                .And.NotContain("Health");
            texts["Unrelated.cs"].Should().Be(UnrelatedSource);
            texts["GameHealthComponent.cs"].Should().Contain("public GameEntity AddHp(int newValue)").And.Contain("public bool hasHp").And.NotContain("Health");

            var compilation = await project.GetCompilationAsync();
            compilation!.GetDiagnostics().Where(d => d.Severity == DiagnosticSeverity.Error).Should().BeEmpty();
        }

        static async Task<Dictionary<string, string>> ApplyFixAsync()
        {
            var (solution, _) = await ApplyFixToSolutionAsync(filesMode: false);

            var texts = new Dictionary<string, string>();
            foreach (var doc in solution.Projects.Single().Documents)
                texts[doc.Name] = (await doc.GetTextAsync()).ToString();

            return texts;
        }

        static async Task<(Solution Solution, Diagnostic[] Diagnostics)> ApplyFixToSolutionAsync(bool filesMode)
        {
            var (solution, diagnostics) = await AnalyzeAsync(filesMode);
            var document = solution.Projects.Single().Documents.Single(d => d.Name == "Components.cs");

            CodeAction? registered = null;
            var context = new CodeFixContext(
                document,
                diagnostics[0],
                (action, _) => registered = action,
                CancellationToken.None);

            await new RenameToCodeFixProvider().RegisterCodeFixesAsync(context);
            registered.Should().NotBeNull("the fix must offer an action for ENT0001");

            var operations = await registered!.GetOperationsAsync(CancellationToken.None);
            return (operations.OfType<ApplyChangesOperation>().Single().ChangedSolution, diagnostics);
        }

        const string FilesOutput = "[assembly: EntitasGeneration(Output = GenerationOutput.Files)]\n";

        static Task<(Solution Solution, Diagnostic[] Diagnostics)> AnalyzeAsync() => AnalyzeAsync(filesMode: false);

        static async Task<(Solution Solution, Diagnostic[] Diagnostics)> AnalyzeAsync(bool filesMode)
        {
            var componentSource = filesMode
                ? ComponentSource.Replace("[assembly: ContextDefinition(\"Game\")]\n", "[assembly: ContextDefinition(\"Game\")]\n" + FilesOutput)
                : ComponentSource;

            var workspace = new AdhocWorkspace();
            var projectId = ProjectId.CreateNewId();

            var solution = workspace.CurrentSolution
                .AddProject(projectId, "TestAsm", "TestAsm", LanguageNames.CSharp)
                .WithProjectMetadataReferences(projectId, References())
                .WithProjectCompilationOptions(projectId,
                    new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary))
                .AddDocument(DocumentId.CreateNewId(projectId), "Components.cs", SourceText.From(componentSource),
                    filePath: "Components.cs")
                .AddDocument(DocumentId.CreateNewId(projectId), "DamageSystem.cs", SourceText.From(SystemSource),
                    filePath: "DamageSystem.cs")
                .AddDocument(DocumentId.CreateNewId(projectId), "Unrelated.cs", SourceText.From(UnrelatedSource),
                    filePath: "Unrelated.cs");

            if (filesMode)
            {
                // What entitas-gen would have written, as documents of the project.
                var plain = await solution.GetProject(projectId)!.GetCompilationAsync();
                foreach (var source in Entitas.SourceGenerator.EntitasGenerators.Generate(plain!))
                {
                    var path = "Generated/" + source.FileName;
                    solution = solution.AddDocument(
                        DocumentId.CreateNewId(projectId),
                        Path.GetFileName(path),
                        SourceText.From(Entitas.SourceGenerator.GeneratedFiles.FileText(source)),
                        filePath: path);
                }
            }

            var compilation = await solution.GetProject(projectId)!.GetCompilationAsync();
            var withAnalyzers = compilation!.WithAnalyzers(
                ImmutableArray.Create<DiagnosticAnalyzer>(new RenameToAnalyzer()));

            var diagnostics = (await withAnalyzers.GetAnalyzerDiagnosticsAsync()).ToArray();
            return (solution, diagnostics);
        }

        static IEnumerable<MetadataReference> References()
        {
            var tpa = (string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!;
            foreach (var path in tpa.Split(Path.PathSeparator))
            {
                if (!string.IsNullOrEmpty(path) && File.Exists(path))
                    yield return MetadataReference.CreateFromFile(path);
            }

            yield return MetadataReference.CreateFromFile(typeof(Entitas.IComponent).Assembly.Location);
            yield return MetadataReference.CreateFromFile(
                typeof(Entitas.CodeGeneration.Attributes.RenameToAttribute).Assembly.Location);
        }
    }
}
