using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Entitas.SourceGenerator;
using Entitas.SourceGenerator.Cli;
using Entitas.SourceGenerator.Rename;
using FluentAssertions;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Xunit;

namespace Entitas.Rename.Tests
{
    /// <summary>
    /// The engine side of "Shift+F6 on the component class, then regenerate" in file mode:
    /// recognising the rename from the generated files about to be deleted
    /// (<see cref="RenameDetector"/>), and planning the usages the IDE left on the old derived
    /// names (<see cref="RenameEngine.CreatePlanForRenamedComponent"/>) — in the declaring
    /// assembly and in one that references it.
    /// </summary>
    public class IdeRenameDetectionTests : RenameTestBase
    {
        static readonly string Root = Path.Combine(Path.GetTempPath(), "entitas-ide-rename");
        static readonly string Output = Path.Combine(Root, "Assets", "Code", "Generated");

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

        const string Tests = @"
public sealed class HealthTests {
    public int Run(GameEntity entity) {
        entity.AddHealth(5);
        return entity.hasHealth ? entity.health.value : 0;
    }
}
";

        static string PathOf(string relative) => Path.Combine(Root, relative.Replace('/', Path.DirectorySeparatorChar));

        /// <summary>User sources by path.</summary>
        static Dictionary<string, string> UserCode(string components = Components) => new Dictionary<string, string>
        {
            [PathOf("Assets/Code/EntitasGeneration.cs")] = Config,
            [PathOf("Assets/Code/Components.cs")] = components,
            [PathOf("Assets/Code/DamageSystem.cs")] = System
        };

        /// <summary>The project as it is on disk in file mode: user code plus the generated files.</summary>
        static Compilation WithGeneratedFiles(IDictionary<string, string> userCode, Dictionary<string, string>? generated = null)
        {
            var user = Compile("Assembly-CSharp", userCode.Select(pair => (pair.Key, pair.Value)).ToArray());
            generated ??= Generated(user);
            return user.AddSyntaxTrees(generated.Select(pair => CSharpSyntaxTree.ParseText(pair.Value, path: pair.Key)));
        }

        static Dictionary<string, string> Generated(Compilation userCode) =>
            EntitasGenerators.Generate(GenerationInput.From(userCode)).Sources.ToDictionary(
                source => Path.GetFullPath(Path.Combine(Output, source.FileName)),
                GeneratedFiles.FileText);

        /// <summary>What Shift+F6 does: the class and every reference to the type, generated files included.</summary>
        static Dictionary<string, string> IdeRename(IDictionary<string, string> files, string oldClassName, string newClassName) =>
            files.ToDictionary(pair => pair.Key, pair => Regex.Replace(pair.Value, $@"\b{oldClassName}\b", newClassName));

        static (RenameDetection Detection, Compilation Project) Detect(Dictionary<string, string> userCode, Dictionary<string, string> generated)
        {
            var project = WithGeneratedFiles(userCode, generated);
            var input = GenerationInput.From(project);
            var sources = EntitasGenerators.Generate(input).Sources;
            return (RenameDetector.Detect(project, input, sources, Output, generated.Keys.ToList()), project);
        }

        static string[] Errors(Compilation compilation) => compilation.GetDiagnostics()
            .Where(d => d.Severity == DiagnosticSeverity.Error)
            .Select(d => d.ToString())
            .ToArray();

        static (Dictionary<string, string> UserCode, Dictionary<string, string> Generated) RenamedInTheIde(
            string oldClassName, string newClassName, Dictionary<string, string>? userCode = null)
        {
            userCode ??= UserCode();
            var generated = Generated(Compile("Assembly-CSharp", userCode.Select(p => (p.Key, p.Value)).ToArray()));
            return (IdeRename(userCode, oldClassName, newClassName), IdeRename(generated, oldClassName, newClassName));
        }

        [Fact]
        public void Recognises_the_rename_and_plans_the_usages_the_ide_left_behind()
        {
            var (userCode, generated) = RenamedInTheIde("HealthComponent", "HpComponent");
            Errors(WithGeneratedFiles(userCode, generated)).Should().BeEmpty("the IDE rename alone keeps the project compiling");

            var (detection, project) = Detect(userCode, generated);

            detection.Warning.Should().BeNull();
            var rename = detection.Rename!;
            rename.Should().NotBeNull();
            rename.OldClassName.Should().Be("HealthComponent");
            rename.NewClassName.Should().Be("HpComponent");
            rename.Plan.NameMap.Should().Contain("AddHealth", "AddHp")
                .And.Contain("hasHealth", "hasHp")
                .And.Contain("isHealthChanged", "isHpChanged")
                .And.Contain("IAnyHealthListener", "IAnyHpListener")
                .And.Contain("OnAnyHealth", "OnAnyHp")
                .And.NotContainKey("HealthComponent", "the IDE renamed the class already");
            rename.Plan.Warnings.Should().BeEmpty();
            rename.Plan.Files.Select(f => f.Path).Should().Equal(new[] { PathOf("Assets/Code/DamageSystem.cs") },
                "generated files are left to regeneration");

            // Update the usages, regenerate: the project compiles under the new names.
            var updated = userCode.ToDictionary(pair => pair.Key, pair => pair.Value);
            foreach (var file in rename.Plan.Files)
                updated[file.Path] = RenameEngine.Apply(updated[file.Path], file);

            updated[PathOf("Assets/Code/DamageSystem.cs")].Should()
                .Contain("IAnyHpListener").And.Contain("entity.hasHp && entity.isHpChanged")
                .And.Contain("GameMatcher.Hp;").And.Contain("OnAnyHp(").And.NotContain("Health");
            Errors(WithGeneratedFiles(updated)).Should().BeEmpty();
        }

        [Fact]
        public void Plans_the_usages_in_an_assembly_that_references_the_generating_one()
        {
            var (userCode, generated) = RenamedInTheIde("HealthComponent", "HpComponent");
            var (detection, project) = Detect(userCode, generated);
            var plan = detection.Rename!.Plan;

            var tests = Compile("Assembly-CSharp-Editor", ("HealthTests.cs", Tests))
                .AddReferences(project.ToMetadataReference());
            Errors(tests).Should().BeEmpty();

            var external = ExternalRename.CollectEdits(tests, plan);

            var updated = RenameEngine.Apply(Tests, external.Files.Single());
            updated.Should().Contain("entity.AddHp(5)").And.Contain("entity.hasHp ? entity.hp.value : 0");

            // Against the regenerated assembly, with the declaring assembly's usages updated too.
            var declaring = userCode.ToDictionary(pair => pair.Key, pair => pair.Value);
            foreach (var file in plan.Files)
                declaring[file.Path] = RenameEngine.Apply(declaring[file.Path], file);
            var regenerated = WithGeneratedFiles(declaring);
            Errors(Compile("Assembly-CSharp-Editor", ("HealthTests.cs", updated)).AddReferences(regenerated.ToMetadataReference()))
                .Should().BeEmpty();
        }

        [Fact]
        public void Two_renames_at_once_are_ambiguous()
        {
            var (userCode, generated) = RenamedInTheIde("HealthComponent", "HpComponent");
            userCode = IdeRename(userCode, "SpeedComponent", "VelocityComponent");
            generated = IdeRename(generated, "SpeedComponent", "VelocityComponent");

            var (detection, _) = Detect(userCode, generated);

            detection.Rename.Should().BeNull();
            detection.Warning.Should().Contain("ambiguous")
                .And.Contain("HealthComponent -> HpComponent")
                .And.Contain("SpeedComponent -> VelocityComponent");
        }

        [Fact]
        public void A_component_deleted_and_another_added_is_not_a_rename()
        {
            var generated = Generated(Compile("Assembly-CSharp", UserCode().Select(p => (p.Key, p.Value)).ToArray()));
            var userCode = UserCode(Components.Replace("class HealthComponent", "class HpComponent"));

            var (detection, _) = Detect(userCode, generated);

            detection.Rename.Should().BeNull();
            detection.Warning.Should().BeNull();
        }

        [Fact]
        public void Nothing_is_left_to_do_after_a_rename_tool_carried_the_rename_through()
        {
            // entitas-rename / [RenameTo] in file mode rewrite the generated files' declarations
            // too; the regeneration that follows must not mistake that for an IDE rename.
            var userCode = UserCode();
            var project = WithGeneratedFiles(userCode);
            var plan = RenameEngine.CreatePlan(project, "Health", "Hp");
            var files = project.SyntaxTrees.ToDictionary(t => t.FilePath, t => t.GetText().ToString());
            foreach (var file in plan.Files)
                files[file.Path] = RenameEngine.Apply(files[file.Path], file);

            var (detection, _) = Detect(
                files.Where(p => !p.Key.StartsWith(Output)).ToDictionary(p => p.Key, p => p.Value),
                files.Where(p => p.Key.StartsWith(Output)).ToDictionary(p => p.Key, p => p.Value));

            detection.Rename.Should().BeNull();
            detection.Warning.Should().BeNull();
        }
    }
}
