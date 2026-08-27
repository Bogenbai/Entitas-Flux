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
    /// Usages of a renamed component living in a DIFFERENT assembly (a test asmdef, an
    /// Editor assembly) — there the generated API is only visible through an assembly
    /// reference, not as source.
    /// </summary>
    public class ExternalRenameTests : RenameTestBase
    {
        const string GameSource = Usings + ContextDefinition + @"
public sealed class HealthComponent : Entitas.IComponent {
    public int value;
}
";

        const string TestAssemblySource = @"
public sealed class Monster {
    public int Health;
}

public sealed class HealthTests {
    public void Run(GameEntity entity, Monster monster) {
        if (entity.hasHealth)
            entity.ReplaceHealth(1);
        else
            entity.AddHealth(5);

        var matcher = GameMatcher.Health;
        var unrelated = monster.Health;
    }
}
";

        [Fact]
        public void Rewrites_the_generated_api_in_a_referencing_assembly()
        {
            var (plan, referencing) = Setup();

            var external = ExternalRename.CollectEdits(referencing, plan);

            external.AssemblyName.Should().Be("TestAsm.Tests");
            var file = external.Files.Single();
            var replacements = file.Edits.Select(e => $"{e.OldText}->{e.NewText}").ToArray();

            replacements.Should().BeEquivalentTo(
                "hasHealth->hasHp",
                "ReplaceHealth->ReplaceHp",
                "AddHealth->AddHp",
                "Health->Hp");

            var updated = RenameEngine.Apply(TestAssemblySource, file);
            updated.Should()
                .Contain("entity.hasHp")
                .And.Contain("entity.ReplaceHp(1)")
                .And.Contain("entity.AddHp(5)")
                .And.Contain("GameMatcher.Hp")
                // Monster.Health belongs to THIS assembly, so it must survive untouched.
                .And.Contain("public int Health;")
                .And.Contain("monster.Health");
        }

        [Fact]
        public void Reports_nothing_for_an_assembly_that_does_not_use_the_component()
        {
            var (plan, _) = Setup();
            var unrelated = Compile("Other", ("Other.cs", "public sealed class Other { public int Value; }"));

            ExternalRename.CollectEdits(unrelated, plan).Files.Should().BeEmpty();
        }

        /// <summary>
        /// Builds the game assembly with its generated API materialised, then a second
        /// assembly that references it the way Unity references Assembly-CSharp.
        /// </summary>
        static (RenamePlan Plan, Compilation Referencing) Setup()
        {
            var game = Compile("TestAsm", ("Components.cs", GameSource));
            var plan = RenameEngine.CreatePlan(game, "Health", "Hp");

            var withGeneratedApi = game.AddSyntaxTrees(EntitasGenerators.Generate(game)
                .Select(source => CSharpSyntaxTree.ParseText(source.Content)));

            var referencing = Compile("TestAsm.Tests", ("HealthTests.cs", TestAssemblySource))
                .AddReferences(withGeneratedApi.ToMetadataReference());

            return (plan, referencing);
        }
    }
}
