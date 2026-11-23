using System;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using FluentAssertions;
using Microsoft.CodeAnalysis;
using Xunit;

namespace Entitas.SourceGenerator.Cli.Tests
{
    /// <summary>
    /// Shift+F6 on a component class in the IDE, then regenerate. The IDE renames the class
    /// and every reference to the type — inside the generated files too — but not the members
    /// the generator derives from the name (AddHealth, hasHealth, GameMatcher.Health, …).
    /// Regenerating used to delete them under the code still using them; now the writer
    /// recognises the rename, asks, and with a yes updates those usages first.
    /// </summary>
    public class IdeRenameTests : IDisposable
    {
        const string Config = @"using Entitas.CodeGeneration.Attributes;
[assembly: ContextDefinition(""Game"")]
[assembly: EntitasGeneration(Output = GenerationOutput.Files)]
";

        const string Components = @"using Entitas;
using Entitas.CodeGeneration.Attributes;
[Watched, Event(EventTarget.Any)]
public sealed class HealthComponent : IComponent { public int value; }
public sealed class SpeedComponent : IComponent { public float value; }
";

        const string System = @"
public sealed class DamageSystem : IAnyHealthListener {
    public void Execute(GameEntity entity) {
        if (entity.hasHealth && entity.isHealthChanged)
            entity.ReplaceHealth(entity.health.value - 1);
        var matcher = GameMatcher.Health;
        entity.AddAnyHealthListener(this);
        entity.ReplaceSpeed(entity.speed.value * 2);
    }
    public void OnAnyHealth(GameEntity entity, int value) { }
}
";

        readonly UnityProjectFixture _project = new UnityProjectFixture();

        public IdeRenameTests()
        {
            _project.Write("Assets/Code/EntitasGeneration.cs", Config);
            _project.Write("Assets/Code/Components.cs", Components);
            _project.Write("Assets/Code/DamageSystem.cs", System);

            _project.Generate().Errors.Should().BeEmpty();
            Errors().Should().BeEmpty("the project compiles before anything is renamed");
        }

        public void Dispose() => _project.Dispose();

        /// <summary>What Shift+F6 does: the class and every reference to the type, generated files included.</summary>
        void IdeRename(string oldClassName, string newClassName)
        {
            foreach (var path in _project.SourceFiles())
            {
                var text = File.ReadAllText(path);
                var renamed = Regex.Replace(text, $@"\b{oldClassName}\b", newClassName);
                if (renamed != text)
                    File.WriteAllText(path, renamed);
            }
        }

        string[] Errors() => _project.Compile().GetDiagnostics()
            .Where(d => d.Severity == DiagnosticSeverity.Error)
            .Select(d => d.ToString())
            .ToArray();

        AssemblyResult Generate(RenameHandling renames) =>
            GeneratedFileWriter.Run(_project.Compile(), _project.Root, new WriterOptions { Renames = renames });

        [Fact]
        public void Asks_first_and_writes_nothing_until_told()
        {
            IdeRename("HealthComponent", "HpComponent");
            Errors().Should().BeEmpty("the IDE rename alone leaves the project compiling");
            var before = _project.GeneratedFiles().ToDictionary(f => f, _project.Read);

            var result = Generate(RenameHandling.Ask);

            result.Errors.Should().BeEmpty();
            var rename = result.Rename!;
            rename.Should().NotBeNull();
            rename.Outcome.Should().Be(RenameOutcome.Pending);
            rename.OldClassName.Should().Be("HealthComponent");
            rename.NewClassName.Should().Be("HpComponent");
            rename.Files.Select(f => _project.Relative(f.Path)).Should().Equal("Assets/Code/DamageSystem.cs");
            rename.UsedIdentifiers.Should().Contain(("hasHealth", "hasHp")).And.Contain(("OnAnyHealth", "OnAnyHp"));

            result.Written.Should().BeEmpty();
            result.Deleted.Should().BeEmpty();
            _project.GeneratedFiles().ToDictionary(f => f, _project.Read).Should().Equal(before,
                "deleting the old generated files is what breaks the usages, and what would make the old names unresolvable");
            _project.Read("Assets/Code/DamageSystem.cs").Should().Be(System);
            GenerationHost.Failed(result, check: false).Should().BeTrue("the CLI exits 1 while a decision is pending");
        }

        [Fact]
        public void Applying_updates_the_usages_then_regenerates_and_the_project_compiles()
        {
            IdeRename("HealthComponent", "HpComponent");

            var result = Generate(RenameHandling.Apply);

            result.Errors.Should().BeEmpty();
            result.Rename!.Outcome.Should().Be(RenameOutcome.Applied);
            result.Rename.EditedFiles.Select(_project.Relative).Should().Equal("Assets/Code/DamageSystem.cs");

            _project.Read("Assets/Code/DamageSystem.cs").Should()
                .Contain("IAnyHpListener")
                .And.Contain("entity.hasHp && entity.isHpChanged")
                .And.Contain("entity.ReplaceHp(entity.hp.value - 1)")
                .And.Contain("GameMatcher.Hp;")
                .And.Contain("entity.AddAnyHpListener(this)")
                .And.Contain("public void OnAnyHp(GameEntity entity, int value)")
                .And.Contain("entity.ReplaceSpeed(entity.speed.value * 2)")
                .And.NotContain("Health");

            _project.Exists("Assets/Code/Generated/Game/Components/GameHpComponent.cs").Should().BeTrue();
            _project.Exists("Assets/Code/Generated/Game/Components/GameHealthComponent.cs").Should().BeFalse();
            Errors().Should().BeEmpty();

            Generate(RenameHandling.Ask).Changed.Should().BeFalse("the rename is complete; nothing is left to detect");
        }

        [Fact]
        public void Applies_only_a_confirmed_rename()
        {
            IdeRename("HealthComponent", "HpComponent");

            var result = GeneratedFileWriter.Run(_project.Compile(), _project.Root, new WriterOptions
            {
                ApplyRenames = new[] { "Assembly-CSharp:SpeedComponent>VelocityComponent" }
            });

            result.Rename!.Outcome.Should().Be(RenameOutcome.Pending, "the user was shown a different rename");
            result.Changed.Should().BeFalse();
            _project.Read("Assets/Code/DamageSystem.cs").Should().Be(System);
        }

        [Fact]
        public void Ignoring_regenerates_without_touching_the_usages()
        {
            IdeRename("HealthComponent", "HpComponent");

            var result = Generate(RenameHandling.Ignore);

            result.Rename!.Outcome.Should().Be(RenameOutcome.Ignored);
            result.Deleted.Select(_project.Relative).Should().Contain("Assets/Code/Generated/Game/Components/GameHealthComponent.cs");
            _project.Read("Assets/Code/DamageSystem.cs").Should().Be(System);
        }

        [Fact]
        public void A_renamed_component_nothing_uses_is_regenerated_without_asking()
        {
            IdeRename("SpeedComponent", "VelocityComponent");
            _project.Write("Assets/Code/DamageSystem.cs", System.Replace("        entity.ReplaceSpeed(entity.speed.value * 2);\n", ""));

            var result = Generate(RenameHandling.Ask);

            result.Rename!.Outcome.Should().Be(RenameOutcome.NothingToUpdate);
            _project.Exists("Assets/Code/Generated/Game/Components/GameVelocityComponent.cs").Should().BeTrue();
            Errors().Should().BeEmpty();
        }

        [Fact]
        public void Two_components_renamed_at_once_are_ambiguous_and_nothing_is_rewritten()
        {
            IdeRename("HealthComponent", "HpComponent");
            IdeRename("SpeedComponent", "VelocityComponent");

            var result = Generate(RenameHandling.Apply);

            result.Rename.Should().BeNull();
            result.Warnings.Should().ContainSingle(w => w.Contains("ambiguous"))
                .Which.Should().Contain("HealthComponent -> HpComponent").And.Contain("SpeedComponent -> VelocityComponent");
            _project.Read("Assets/Code/DamageSystem.cs").Should().Be(System);
            result.Deleted.Should().NotBeEmpty("ambiguity is reported, and generation goes on as it always did");
        }

        [Fact]
        public void A_rename_together_with_a_new_component_is_ambiguous_and_nothing_is_rewritten()
        {
            IdeRename("HealthComponent", "HpComponent");
            _project.Write("Assets/Code/Mana.cs", "public sealed class ManaComponent : Entitas.IComponent { public int value; }\n");

            var result = Generate(RenameHandling.Apply);

            result.Rename.Should().BeNull();
            result.Warnings.Should().ContainSingle(w => w.Contains("ambiguous"));
            _project.Read("Assets/Code/DamageSystem.cs").Should().Be(System);
        }

        [Fact]
        public void A_component_deleted_and_another_added_is_not_a_rename()
        {
            _project.Write("Assets/Code/Components.cs", Components.Replace(
                "public sealed class HealthComponent : IComponent { public int value; }",
                "public sealed class HpComponent : IComponent { public int value; }"));

            var result = Generate(RenameHandling.Apply);

            result.Rename.Should().BeNull("the old files reference HealthComponent, which no longer exists");
            result.Warnings.Should().BeEmpty();
            _project.Read("Assets/Code/DamageSystem.cs").Should().Be(System);
            _project.Exists("Assets/Code/Generated/Game/Components/GameHealthComponent.cs").Should().BeFalse();
        }

        [Fact]
        public void The_editor_request_updates_usages_in_assemblies_that_reference_the_generating_one()
        {
            _project.Write("Assets/Editor/HealthInspector.cs", @"
public static class HealthInspector {
    public static int Read(GameEntity entity) => entity.hasHealth ? entity.health.value : 0;
}
");
            IdeRename("HealthComponent", "HpComponent");

            var ask = GenerationHost.RunRequest(Request());
            var pending = ask.assemblies.Single(a => a.name == "Assembly-CSharp").rename;
            pending.state.Should().Be("pending");
            pending.oldName.Should().Be("HealthComponent");
            pending.newName.Should().Be("HpComponent");
            pending.files.Should().BeEquivalentTo("Assets/Code/DamageSystem.cs", "Assets/Editor/HealthInspector.cs");
            pending.identifiers.Should().Contain("hasHealth -> hasHp");
            _project.Exists("Assets/Code/Generated/Game/Components/GameHealthComponent.cs").Should().BeTrue();

            var request = Request();
            request.applyRenames.Add(pending.key);
            var applied = GenerationHost.RunRequest(request).assemblies.Single(a => a.name == "Assembly-CSharp");

            applied.errors.Should().BeEmpty();
            applied.rename.state.Should().Be("applied");
            applied.rename.files.Should().BeEquivalentTo("Assets/Code/DamageSystem.cs", "Assets/Editor/HealthInspector.cs");
            _project.Read("Assets/Editor/HealthInspector.cs").Should().Contain("entity.hasHp ? entity.hp.value : 0");
            applied.deleted.Should().Contain("Assets/Code/Generated/Game/Components/GameHealthComponent.cs");
            Errors().Should().BeEmpty();
        }

        GenerationRequest Request()
        {
            var runtime = _project.SourceFiles().Where(p => !p.Contains($"{Path.DirectorySeparatorChar}Editor{Path.DirectorySeparatorChar}"));
            var editor = _project.SourceFiles().Where(p => p.Contains($"{Path.DirectorySeparatorChar}Editor{Path.DirectorySeparatorChar}"));

            var request = new GenerationRequest { projectRoot = _project.Root };
            request.assemblies.Add(new RequestedAssembly
            {
                name = "Assembly-CSharp",
                sourceFiles = runtime.Select(_project.Relative).ToList(),
                references = UnityProjectFixture.References().ToList()
            });
            request.assemblies.Add(new RequestedAssembly
            {
                name = "Assembly-CSharp-Editor",
                sourceFiles = editor.Select(_project.Relative).ToList(),
                references = UnityProjectFixture.References()
                    .Append(_project.PathOf("Library/ScriptAssemblies/Assembly-CSharp.dll"))
                    .ToList()
            });
            return request;
        }
    }
}
