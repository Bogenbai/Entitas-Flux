using System.Linq;
using Entitas.SourceGenerator;
using Entitas.SourceGenerator.Rename;
using FluentAssertions;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Xunit;

namespace Entitas.Rename.Tests
{
    /// <summary>
    /// Renaming with Output = GenerationOutput.Files: the generated API is in files of the
    /// project, declared there rather than by the source generator. The rename must resolve
    /// against those files, rewrite their declarations along with every usage — so the
    /// project compiles the moment the rename is applied — and regenerating afterwards must
    /// leave it compiling with the files under their new names.
    /// </summary>
    public class FileModeRenameTests : RenameTestBase
    {
        const string Config = Usings + ContextDefinition +
            "[assembly: EntitasGeneration(Output = GenerationOutput.Files)]\n";

        const string Components = Usings + @"
[Watched, Event(EventTarget.Any)]
public sealed class HealthComponent : Entitas.IComponent { public int value; }
public sealed class SpeedComponent : Entitas.IComponent { public float value; }
";

        const string System = @"
public sealed class DamageSystem : IAnyHealthListener {
    public void Execute(GameEntity entity) {
        if (entity.hasHealth && entity.isHealthChanged)
            entity.ReplaceHealth(entity.health.value - 1);
        var matcher = GameMatcher.Health;
        entity.AddAnyHealthListener(this);
    }
    public void OnAnyHealth(GameEntity entity, int value) { }
}
";

        static Compilation Project(string components, string system, Compilation? generateFrom = null)
        {
            var project = Compile(
                ("Assets/Code/EntitasGeneration.cs", Config),
                ("Assets/Code/Components.cs", components),
                ("Assets/Code/DamageSystem.cs", system));

            var sources = EntitasGenerators.Generate(GenerationInput.From(generateFrom ?? project)).Sources;
            return project.AddSyntaxTrees(sources.Select(source => CSharpSyntaxTree.ParseText(
                GeneratedFiles.FileText(source), path: "Assets/Code/Generated/" + source.FileName)));
        }

        static string[] Errors(Compilation compilation) => compilation.GetDiagnostics()
            .Where(d => d.Severity == DiagnosticSeverity.Error)
            .Select(d => d.ToString())
            .ToArray();

        [Fact]
        public void The_project_compiles_right_after_the_rename_and_after_regenerating()
        {
            var project = Project(Components, System);
            Errors(project).Should().BeEmpty();

            var plan = RenameEngine.CreatePlan(project, "Health", "Hp");

            plan.Warnings.Should().BeEmpty();
            plan.Files.Select(f => f.Path).Should().Contain("Assets/Code/DamageSystem.cs")
                .And.Contain("Assets/Code/Generated/Game/Components/GameHealthComponent.cs");

            // Straight after the rename: generated files still have their old names, but
            // declare and use the new ones.
            var renamed = ApplyPlan(project, plan);
            var afterRename = Compile(renamed.Select(pair => (pair.Key, pair.Value)).ToArray());
            Errors(afterRename).Should().BeEmpty();
            renamed["Assets/Code/DamageSystem.cs"].Should()
                .Contain("entity.hasHp && entity.isHpChanged")
                .And.Contain("IAnyHpListener")
                .And.NotContain("Health");

            // Regenerating replaces them with what the generator now produces.
            var userCode = renamed.Where(pair => !pair.Key.StartsWith("Assets/Code/Generated/")).ToArray();
            var regenerated = Project(userCode.Single(p => p.Key.EndsWith("Components.cs")).Value,
                                      userCode.Single(p => p.Key.EndsWith("DamageSystem.cs")).Value);
            Errors(regenerated).Should().BeEmpty();
            regenerated.SyntaxTrees.Select(t => t.FilePath).Should()
                .Contain("Assets/Code/Generated/Game/Components/GameHpComponent.cs")
                .And.NotContain(path => path.Contains("Health"));
        }

        [Fact]
        public void Stale_files_do_not_hide_the_component_from_the_rename()
        {
            // Files generated before Speed existed: Speed's API is missing from disk, so its
            // usages cannot resolve — the rename still finds the component and its names.
            var before = Compile(
                ("Assets/Code/EntitasGeneration.cs", Config),
                ("Assets/Code/Components.cs", Usings + "public sealed class HealthComponent : Entitas.IComponent { public int value; }\n"));
            var project = Project(Components, System, generateFrom: before);

            var plan = RenameEngine.CreatePlan(project, "Speed", "Velocity");

            plan.NameMap.Should().ContainKey("AddSpeed").WhoseValue.Should().Be("AddVelocity");
            plan.Files.Select(f => f.Path).Should().Contain("Assets/Code/Components.cs");
        }
    }
}
