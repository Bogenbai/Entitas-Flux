using System.Collections.Generic;
using System.Linq;
using FluentAssertions;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Xunit;

namespace Entitas.SourceGenerator.Tests
{
    /// <summary>
    /// The compiler's side of Output = GenerationOutput.Files: discovery never reads the
    /// generated files back, the source generator stands down instead of defining every
    /// type twice, and it says so when the files on disk no longer match the components.
    /// (Writing the files is entitas-gen's job; its tests live with it.)
    /// </summary>
    public class FileModeTests
    {
        const string Config = @"
using Entitas.CodeGeneration.Attributes;
[assembly: ContextDefinition(""Game"")]
[assembly: EntitasGeneration(Output = GenerationOutput.Files)]
";

        const string Health = "public sealed class HealthComponent : Entitas.IComponent { public int value; }\n";
        const string Armor = "[Entitas.CodeGeneration.Attributes.Watched] public sealed class ArmorComponent : Entitas.IComponent { public int value; }\n";

        static CSharpCompilation Project(string config, string components) =>
            OutputModes.Compile("FileModeAsm", ("Assets/Code/EntitasGeneration.cs", config), ("Assets/Code/Components.cs", components));

        static SyntaxTree[] Files(Compilation compilation) =>
            OutputModes.GeneratedFileTrees(compilation, "Assets/Code/Generated");

        static GeneratorRunResult RunSourceGenerator(Compilation compilation) =>
            CSharpGeneratorDriver.Create(new EntitasIncrementalGenerator()).RunGenerators(compilation).GetRunResult().Results.Single();

        // -- discovery ----------------------------------------------------------

        [Fact]
        public void Discovery_does_not_read_the_generated_files_back()
        {
            // The files declare components of their own (ArmorChanged, from [Watched]).
            var project = Project(Config, Health + Armor);
            var withFiles = project.AddSyntaxTrees(Files(project));

            withFiles.GetTypeByMetadataName("ArmorChanged").Should().NotBeNull("the generated marker component is in the assembly");
            GenerationInput.From(withFiles).Should().Be(GenerationInput.From(project));
        }

        [Fact]
        public void Generation_does_not_need_the_project_to_compile()
        {
            // A component typed with generated GameEntity while no files exist yet, a syntax
            // error elsewhere, and a stale generated file for a component that is gone.
            const string components = Health + @"
public sealed class TargetComponent : Entitas.IComponent { public GameEntity value; }
public class Broken { void M() { this does not compile } }
";
            var clean = Project(Config, Health + "public sealed class TargetComponent : Entitas.IComponent { public GameEntity value; }\n");

            var stale = Project(Config, Health + Armor);
            var broken = Project(Config, components).AddSyntaxTrees(Files(stale));
            broken.GetDiagnostics().Should().Contain(d => d.Severity == DiagnosticSeverity.Error);

            EntitasGenerators.Generate(GenerationInput.From(broken)).Sources
                .Select(s => (s.FileName, s.Content))
                .Should().Equal(EntitasGenerators.Generate(GenerationInput.From(clean)).Sources.Select(s => (s.FileName, s.Content)));
        }

        // -- the source generator in file mode --------------------------------------

        [Fact]
        public void Source_generator_adds_nothing_when_the_files_are_current()
        {
            var project = Project(Config, Health + Armor);
            var result = RunSourceGenerator(project.AddSyntaxTrees(Files(project)));

            result.GeneratedSources.Should().BeEmpty();
            result.Diagnostics.Should().BeEmpty();
        }

        [Fact]
        public void Source_generator_reports_files_that_were_never_written()
        {
            var result = RunSourceGenerator(Project(Config, Health));

            result.GeneratedSources.Should().BeEmpty();
            result.Diagnostics.Should().ContainSingle(d => d.Id == EntitasDiagnostics.StaleGeneratedFilesId)
                .Which.GetMessage().Should().Contain("missing").And.Contain("Game/Components/GameHealthComponent.cs");
        }

        [Fact]
        public void Source_generator_reports_a_component_change_the_files_do_not_have_yet()
        {
            var before = Project(Config, Health);
            var after = Project(Config, "public sealed class HealthComponent : Entitas.IComponent { public int value; public int max; }\n");

            var result = RunSourceGenerator(after.AddSyntaxTrees(Files(before)));

            result.Diagnostics.Should().ContainSingle(d => d.Id == EntitasDiagnostics.StaleGeneratedFilesId)
                .Which.GetMessage().Should().Contain("outdated").And.Contain("Game/Components/GameHealthComponent.cs");
        }

        [Fact]
        public void Source_generator_reports_files_of_a_deleted_component()
        {
            var before = Project(Config, Health + Armor);
            var after = Project(Config, Health);

            var result = RunSourceGenerator(after.AddSyntaxTrees(Files(before)));

            result.Diagnostics.Should().ContainSingle(d => d.Id == EntitasDiagnostics.StaleGeneratedFilesId)
                .Which.GetMessage().Should().Contain("no longer generated").And.Contain("GameArmorComponent.cs");
        }

        [Fact]
        public void Line_endings_do_not_make_files_stale()
        {
            var project = Project(Config, Health);
            var crlf = Files(project)
                .Select(tree => CSharpSyntaxTree.ParseText(tree.GetText().ToString().Replace("\n", "\r\n"), path: tree.FilePath))
                .ToArray();

            RunSourceGenerator(project.AddSyntaxTrees(crlf)).Diagnostics.Should().BeEmpty();
        }

        [Fact]
        public void An_explicit_output_path_is_matched_as_a_path_segment()
        {
            // Unity compiles with project-relative paths, an IDE with absolute ones.
            const string config = @"
using Entitas.CodeGeneration.Attributes;
[assembly: ContextDefinition(""Game"")]
[assembly: EntitasGeneration(Output = GenerationOutput.Files, OutputPath = ""Assets/Gen"")]
";
            var project = Project(config, Health);
            var sources = EntitasGenerators.Generate(GenerationInput.From(project)).Sources;

            foreach (var root in new[] { "", "/Users/me/MyGame/", "C:\\Projects\\MyGame\\" })
            {
                var files = sources
                    .Select(s => CSharpSyntaxTree.ParseText(GeneratedFiles.FileText(s), path: root + "Assets/Gen/" + s.FileName))
                    .ToArray();

                RunSourceGenerator(project.AddSyntaxTrees(files)).Diagnostics.Should().BeEmpty($"root '{root}'");
            }
        }

        // -- back in source generator mode ---------------------------------------------

        [Fact]
        public void Leftover_files_pause_the_source_generator_instead_of_defining_everything_twice()
        {
            var filesMode = Project(Config, Health);
            var sourceGeneratorMode = Project(@"
using Entitas.CodeGeneration.Attributes;
[assembly: ContextDefinition(""Game"")]
", Health).AddSyntaxTrees(Files(filesMode));

            CSharpGeneratorDriver.Create(new EntitasIncrementalGenerator())
                .RunGeneratorsAndUpdateCompilation(sourceGeneratorMode, out var output, out var diagnostics);

            diagnostics.Should().ContainSingle(d => d.Id == EntitasDiagnostics.LeftoverGeneratedFilesId)
                .Which.GetMessage().Should().Contain("Assets/Code/Generated");
            output.GetDiagnostics().Where(d => d.Severity == DiagnosticSeverity.Error).Should().BeEmpty(
                "the leftover files still compile on their own; adding the generated code again would not");
        }

        [Fact]
        public void Default_mode_is_untouched_by_all_of_this()
        {
            var project = OutputModes.Compile("DefaultAsm", ("Components.cs", @"
[assembly: Entitas.CodeGeneration.Attributes.ContextDefinition(""Game"")]
" + Health));

            var result = RunSourceGenerator(project);
            result.GeneratedSources.Should().NotBeEmpty();
            result.Diagnostics.Should().BeEmpty();
        }

        // -- recognising our own files -------------------------------------------------

        [Theory]
        [InlineData("// <auto-generated/>\n// Generated by Entitas-Flux. Do not edit.\nclass A {}", true)]
        [InlineData("// <auto-generated/>\r\n// Generated by Entitas-Flux. Do not edit.\r\nclass A {}", true)]
        [InlineData("//------------------------------------------------------------------------------\n// <auto-generated>\n//     This code was generated by the Entitas source generator.\n", true)]
        [InlineData("// <auto-generated/>\nclass A {}", false)]
        [InlineData("// <auto-generated/>\n// something else\n// Generated by Entitas-Flux.\n", false)]
        [InlineData("using X;\n// Generated by Entitas-Flux.\n", false)]
        [InlineData("", false)]
        public void Only_files_starting_with_the_marker_are_ours(string text, bool generated)
        {
            // A positive answer licenses deleting the file, so this must never guess.
            GeneratedFiles.IsGenerated(text).Should().Be(generated);
        }

        // -- incrementality --------------------------------------------------------

        [Fact]
        public void An_edit_that_touches_no_component_skips_generation_in_file_mode_too()
        {
            var project = Project(Config, Health);
            var withFiles = project.AddSyntaxTrees(Files(project));
            var system = CSharpSyntaxTree.ParseText("public class DamageSystem { void M(GameEntity e) { e.ReplaceHealth(1); } }", path: "Assets/Code/DamageSystem.cs");
            var edited = CSharpSyntaxTree.ParseText("public class DamageSystem { void M(GameEntity e) { var d = 2; e.ReplaceHealth(d); } }", path: "Assets/Code/DamageSystem.cs");

            GeneratorDriver driver = CSharpGeneratorDriver.Create(
                new[] { new EntitasIncrementalGenerator().AsSourceGenerator() },
                driverOptions: new GeneratorDriverOptions(IncrementalGeneratorOutputKind.None, trackIncrementalGeneratorSteps: true));

            driver = driver.RunGenerators(withFiles.AddSyntaxTrees(system));
            driver = driver.RunGenerators(withFiles.AddSyntaxTrees(edited));

            var reasons = driver.GetRunResult().Results.Single().TrackedOutputSteps
                .SelectMany(step => step.Value)
                .SelectMany(step => step.Outputs)
                .Select(output => output.Reason)
                .ToArray();

            reasons.Should().NotBeEmpty();
            reasons.Should().AllSatisfy(reason => reason.Should().BeOneOf(
                IncrementalStepRunReason.Cached, IncrementalStepRunReason.Unchanged));
        }
    }
}
