using System;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using FluentAssertions;
using Xunit;

namespace Entitas.SourceGenerator.Cli.Tests
{
    /// <summary>
    /// The writer behind Output = GenerationOutput.Files, on real folders. What these pin
    /// down is what keeps Unity calm: an unchanged component writes nothing (every write is
    /// a reimport and a recompile), a renamed or deleted one leaves nothing behind (a
    /// stale file defines its types twice), and nothing that is not ours is ever touched.
    /// </summary>
    public class GeneratedFileWriterTests : IDisposable
    {
        const string Config = @"using Entitas.CodeGeneration.Attributes;
[assembly: ContextDefinition(""Game"")]
[assembly: ContextDefinition(""Input"")]
[assembly: EntitasGeneration(Output = GenerationOutput.Files)]
";

        const string Components = @"using Entitas;
using Entitas.CodeGeneration.Attributes;
[Game] public sealed class HealthComponent : IComponent { public int value; }
[Game, Watched] public sealed class ArmorComponent : IComponent { public int value; }
[Input, Unique] public sealed class PointerComponent : IComponent { public int value; }
";

        readonly UnityProjectFixture _project = new UnityProjectFixture();

        public GeneratedFileWriterTests()
        {
            _project.Write("Assets/Code/EntitasGeneration.cs", Config);
            _project.Write("Assets/Code/Components.cs", Components);
        }

        public void Dispose() => _project.Dispose();

        [Fact]
        public void Writes_one_file_per_generated_source_next_to_the_context_declaration()
        {
            var result = _project.Generate();

            result.Errors.Should().BeEmpty();
            result.Mode.Should().Be(GenerationMode.Files);
            _project.Relative(result.OutputDirectory!).Should().Be("Assets/Code/Generated");

            var expected = EntitasGenerators.Generate(GenerationInput.From(_project.Compile())).Sources;
            result.Written.Select(_project.Relative).Should().BeEquivalentTo(
                expected.Select(source => "Assets/Code/Generated/" + source.FileName));

            foreach (var source in expected)
                _project.Read("Assets/Code/Generated/" + source.FileName).Should().Be(GeneratedFiles.FileText(source));

            _project.Exists("Assets/Code/Generated/Game/Components/GameHealthComponent.cs").Should().BeTrue();
            _project.Exists("Assets/Code/Generated/Input/InputContext.cs").Should().BeTrue();
        }

        [Fact]
        public void Gives_new_files_and_folders_a_meta_whose_guid_depends_only_on_the_path()
        {
            _project.Generate();

            using var other = new UnityProjectFixture();
            other.Write("Assets/Code/EntitasGeneration.cs", Config);
            other.Write("Assets/Code/Components.cs", Components);
            other.Generate();

            foreach (var file in _project.GeneratedFiles())
            {
                var meta = _project.Read(file + ".meta");
                meta.Should().Contain("MonoImporter:");
                Guid(meta).Should().Be(Guid(other.Read(file + ".meta")), $"{file} on two machines must get the same GUID");
            }

            _project.Read("Assets/Code/Generated.meta").Should().Contain("folderAsset: yes");
            _project.Read("Assets/Code/Generated/Game/Components.meta").Should().Contain("folderAsset: yes");
        }

        [Fact]
        public void A_second_run_writes_nothing()
        {
            _project.Generate();
            var stamps = _project.GeneratedFiles().ToDictionary(f => f, f => File.GetLastWriteTimeUtc(_project.PathOf(f)));

            var result = _project.Generate();

            result.Written.Should().BeEmpty("an unchanged component must not cost Unity a reimport");
            result.Deleted.Should().BeEmpty();
            result.Unchanged.Should().Be(stamps.Count);
            foreach (var stamp in stamps)
                File.GetLastWriteTimeUtc(_project.PathOf(stamp.Key)).Should().Be(stamp.Value);
        }

        [Fact]
        public void A_checkout_that_converted_line_endings_is_not_a_change()
        {
            _project.Generate();
            foreach (var file in _project.GeneratedFiles())
                _project.Write(file, _project.Read(file).Replace("\n", "\r\n"));

            _project.Generate().Written.Should().BeEmpty();
        }

        [Fact]
        public void Rewrites_only_the_files_a_component_change_affects()
        {
            _project.Generate();
            _project.Write("Assets/Code/Components.cs", Components.Replace(
                "HealthComponent : IComponent { public int value; }",
                "HealthComponent : IComponent { public int value; public int max; }"));

            var result = _project.Generate();

            result.Written.Select(_project.Relative).Should().BeEquivalentTo(
                "Assets/Code/Generated/Game/Components/GameHealthComponent.cs");
            result.Deleted.Should().BeEmpty();
        }

        [Fact]
        public void Deletes_the_files_of_a_deleted_component_with_their_meta()
        {
            _project.Generate();
            _project.Write("Assets/Code/Components.cs", string.Join("\n", Components.Split('\n').Where(l => !l.Contains("Armor"))));

            var result = _project.Generate();

            result.Deleted.Select(_project.Relative).Should().Contain(new[]
            {
                "Assets/Code/Generated/Game/Components/GameArmorComponent.cs",
                "Assets/Code/Generated/Game/Components/GameArmorChangedComponent.cs"
            });
            foreach (var deleted in result.Deleted.Select(_project.Relative))
            {
                _project.Exists(deleted).Should().BeFalse();
                _project.Exists(deleted + ".meta").Should().BeFalse();
            }

            _project.Generate().Deleted.Should().BeEmpty();
        }

        [Fact]
        public void A_renamed_component_moves_its_files()
        {
            _project.Generate();
            _project.Write("Assets/Code/Components.cs", Components.Replace("HealthComponent", "HpComponent"));

            var result = _project.Generate();

            result.Deleted.Select(_project.Relative).Should().Contain("Assets/Code/Generated/Game/Components/GameHealthComponent.cs");
            result.Written.Select(_project.Relative).Should().Contain("Assets/Code/Generated/Game/Components/GameHpComponent.cs");
            _project.GeneratedFiles().Should().NotContain(f => f.Contains("Health"));
        }

        [Fact]
        public void Removes_folders_left_empty_but_never_the_project_folders()
        {
            _project.Generate();
            _project.Exists("Assets/Code/Generated/Input/Components").Should().BeTrue();

            // The only Input component goes; the Input context itself stays.
            _project.Write("Assets/Code/Components.cs", string.Join("\n", Components.Split('\n').Where(l => !l.Contains("Pointer"))));
            _project.Generate();

            _project.Exists("Assets/Code/Generated/Input/Components").Should().BeFalse();
            _project.Exists("Assets/Code/Generated/Input/Components.meta").Should().BeFalse();
            _project.Exists("Assets/Code/Generated/Input/InputContext.cs").Should().BeTrue();
        }

        [Fact]
        public void Never_touches_a_file_it_did_not_write()
        {
            _project.Generate();
            const string notes = "// my notes\npublic static class Notes { }\n";
            _project.Write("Assets/Code/Generated/Notes.cs", notes);
            const string hijacked = "public partial class GameEntity { }\n";
            _project.Write("Assets/Code/Generated/Game/Components/GameHealthComponent.cs", hijacked);

            var result = _project.Generate();

            _project.Read("Assets/Code/Generated/Notes.cs").Should().Be(notes);
            _project.Read("Assets/Code/Generated/Game/Components/GameHealthComponent.cs").Should().Be(hijacked);
            result.Errors.Should().ContainSingle().Which.Should().Contain("GameHealthComponent.cs").And.Contain("not written by Entitas-Flux");

            // Switching back to the source generator clears our files, still not theirs.
            _project.Write("Assets/Code/EntitasGeneration.cs", Config.Replace("[assembly: EntitasGeneration(Output = GenerationOutput.Files)]", ""));
            _project.Generate();
            _project.Read("Assets/Code/Generated/Notes.cs").Should().Be(notes);
        }

        [Fact]
        public void Moving_the_output_path_moves_every_file()
        {
            _project.Generate();
            _project.Write("Assets/Code/EntitasGeneration.cs", Config.Replace(
                "Output = GenerationOutput.Files", "Output = GenerationOutput.Files, OutputPath = \"Assets/Gen\""));

            var result = _project.Generate();

            result.Errors.Should().BeEmpty();
            _project.Exists("Assets/Code/Generated").Should().BeFalse("the old folder held only generated files");
            _project.Exists("Assets/Code/Generated.meta").Should().BeFalse();
            _project.GeneratedFiles().Should().OnlyContain(f => f.StartsWith("Assets/Gen/", StringComparison.Ordinal));
            _project.Exists("Assets/Code/Components.cs").Should().BeTrue();
        }

        [Fact]
        public void Back_in_source_generator_mode_the_generated_files_are_deleted()
        {
            _project.Generate();
            _project.Write("Assets/Code/EntitasGeneration.cs", Config.Replace("[assembly: EntitasGeneration(Output = GenerationOutput.Files)]", ""));

            var result = _project.Generate();

            result.Mode.Should().Be(GenerationMode.SourceGenerator);
            _project.GeneratedFiles().Should().BeEmpty();
            _project.Exists("Assets/Code/Generated").Should().BeFalse();
            _project.Exists("Assets/Code/EntitasGeneration.cs").Should().BeTrue();
        }

        [Fact]
        public void Refuses_an_output_path_outside_the_project()
        {
            _project.Write("Assets/Code/EntitasGeneration.cs", Config.Replace(
                "Output = GenerationOutput.Files", "Output = GenerationOutput.Files, OutputPath = \"../elsewhere\""));

            var result = _project.Generate();

            result.Errors.Should().ContainSingle().Which.Should().Contain("not inside the project");
            result.Written.Should().BeEmpty();
        }

        [Fact]
        public void Still_generates_when_the_project_does_not_compile()
        {
            _project.Generate();
            _project.Write("Assets/Code/Broken.cs", "public class Broken { void M() { this is not C# } }");
            _project.Write("Assets/Code/Components.cs", Components.Replace("HealthComponent", "HpComponent"));

            var result = _project.Generate();

            result.Errors.Should().BeEmpty();
            result.Written.Select(_project.Relative).Should().Contain("Assets/Code/Generated/Game/Components/GameHpComponent.cs");
        }

        [Fact]
        public void Check_mode_reports_what_is_stale_and_writes_nothing()
        {
            var first = _project.Generate(check: true);
            first.Staleness!.Missing.Should().NotBeEmpty();
            _project.GeneratedFiles().Should().BeEmpty();

            _project.Generate();
            _project.Generate(check: true).Staleness!.IsUpToDate.Should().BeTrue();

            _project.Write("Assets/Code/Components.cs", Components.Replace("HealthComponent", "HpComponent"));
            var stale = _project.Generate(check: true).Staleness!;
            stale.Missing.Should().Contain("Game/Components/GameHpComponent.cs");
            stale.Orphans.Should().Contain(path => path.EndsWith("GameHealthComponent.cs", StringComparison.Ordinal));
            _project.Exists("Assets/Code/Generated/Game/Components/GameHealthComponent.cs").Should().BeTrue();
        }

        [Fact]
        public void Lists_the_files_whose_edits_can_change_the_generated_code()
        {
            _project.Write("Assets/Code/DamageSystem.cs", "public class DamageSystem { }");

            var result = _project.Generate();

            result.ComponentFiles.Should().BeEquivalentTo("Assets/Code/Components.cs", "Assets/Code/EntitasGeneration.cs");
        }

        static string Guid(string meta) => Regex.Match(meta, "guid: ([0-9a-f]{32})").Groups[1].Value;
    }
}
