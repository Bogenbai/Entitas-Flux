using Entitas.SourceGenerator.Rename;
using System;
using System.Linq;
using FluentAssertions;
using Xunit;

namespace Entitas.Rename.Tests
{
    public class RenameEngineTests : RenameTestBase
    {
        const string Component =
            Usings + ContextDefinition + @"
public sealed class HealthComponent : Entitas.IComponent {
    public int value;
}
";

        const string System = @"
public sealed class DamageSystem {
    public void Execute(GameEntity entity) {
        if (entity.hasHealth)
            entity.ReplaceHealth(entity.health.value - 1);
        else
            entity.AddHealth(10);

        entity.SafeRemoveHealth();
        entity.RemoveHealth();

        var matcher = GameMatcher.Health;
        var index = GameComponentsLookup.Health;
    }
}
";

        [Fact]
        public void Maps_the_whole_generated_entity_api()
        {
            var compilation = Compile(("Components.cs", Component), ("DamageSystem.cs", System));

            var plan = RenameEngine.CreatePlan(compilation, "Health", "Hp");

            plan.NameMap.Should().Contain(new[]
            {
                new System.Collections.Generic.KeyValuePair<string, string>("HealthComponent", "HpComponent"),
                new System.Collections.Generic.KeyValuePair<string, string>("Health", "Hp"),
                new System.Collections.Generic.KeyValuePair<string, string>("health", "hp"),
                new System.Collections.Generic.KeyValuePair<string, string>("hasHealth", "hasHp"),
                new System.Collections.Generic.KeyValuePair<string, string>("AddHealth", "AddHp"),
                new System.Collections.Generic.KeyValuePair<string, string>("ReplaceHealth", "ReplaceHp"),
                new System.Collections.Generic.KeyValuePair<string, string>("RemoveHealth", "RemoveHp"),
                new System.Collections.Generic.KeyValuePair<string, string>("SafeRemoveHealth", "SafeRemoveHp")
            });
        }

        [Fact]
        public void Rewrites_usages_so_the_project_still_compiles()
        {
            var compilation = Compile(("Components.cs", Component), ("DamageSystem.cs", System));

            var plan = RenameEngine.CreatePlan(compilation, "Health", "Hp");
            var renamed = ApplyPlan(compilation, plan);

            renamed["Components.cs"].Should().Contain("class HpComponent").And.NotContain("HealthComponent");
            renamed["DamageSystem.cs"].Should()
                .Contain("entity.hasHp")
                .And.Contain("entity.ReplaceHp(entity.hp.value - 1)")
                .And.Contain("entity.SafeRemoveHp()")
                .And.Contain("GameMatcher.Hp")
                .And.Contain("GameComponentsLookup.Hp")
                .And.NotContain("Health");

            ErrorsAfterRename(compilation, plan).Should().BeEmpty();
        }

        [Fact]
        public void Leaves_same_named_members_of_unrelated_types_alone()
        {
            const string unrelated = @"
public sealed class Monster {
    public int Health;
    public int health;
}

public sealed class Report {
    public int Total(Monster monster) => monster.Health + monster.health;
    public int Health { get; set; }
}
";
            var compilation = Compile(
                ("Components.cs", Component),
                ("DamageSystem.cs", System),
                ("Unrelated.cs", unrelated));

            var plan = RenameEngine.CreatePlan(compilation, "Health", "Hp");
            var renamed = ApplyPlan(compilation, plan);

            renamed["Unrelated.cs"].Should().Be(unrelated);
            plan.Files.Should().NotContain(f => f.Path == "Unrelated.cs");
        }

        [Fact]
        public void Maps_the_unique_context_api()
        {
            var compilation = Compile(("Components.cs",
                Usings + ContextDefinition + @"
[Unique]
public sealed class HealthComponent : Entitas.IComponent { public int value; }
"));

            var plan = RenameEngine.CreatePlan(compilation, "Health", "Hp");

            plan.NameMap.Should().ContainKey("SetHealth").WhoseValue.Should().Be("SetHp");
            plan.NameMap.Should().ContainKey("healthEntity").WhoseValue.Should().Be("hpEntity");
        }

        [Fact]
        public void Maps_the_flag_property_of_a_flag_component()
        {
            var compilation = Compile(("Components.cs",
                Usings + ContextDefinition + @"
public sealed class DestroyedComponent : Entitas.IComponent { }
"));

            var plan = RenameEngine.CreatePlan(compilation, "Destroyed", "Doomed");

            plan.NameMap.Should().ContainKey("isDestroyed").WhoseValue.Should().Be("isDoomed");
        }

        [Fact]
        public void Maps_watched_marker_components()
        {
            var compilation = Compile(("Components.cs",
                Usings + ContextDefinition + @"
[Watched]
public sealed class HealthComponent : Entitas.IComponent { public int value; }
"));

            var plan = RenameEngine.CreatePlan(compilation, "Health", "Hp");

            plan.NameMap.Should().ContainKey("HealthChanged").WhoseValue.Should().Be("HpChanged");
            plan.NameMap.Should().ContainKey("isHealthChanged").WhoseValue.Should().Be("isHpChanged");
        }

        [Fact]
        public void Maps_event_listeners()
        {
            var compilation = Compile(("Components.cs",
                Usings + ContextDefinition + @"
[Event(EventTarget.Self)]
public sealed class HealthComponent : Entitas.IComponent { public int value; }
"));

            var plan = RenameEngine.CreatePlan(compilation, "Health", "Hp");

            plan.NameMap.Should().ContainKey("IHealthListener").WhoseValue.Should().Be("IHpListener");
            plan.NameMap.Should().ContainKey("OnHealth").WhoseValue.Should().Be("OnHp");
            plan.NameMap.Should().ContainKey("AddHealthListener").WhoseValue.Should().Be("AddHpListener");
        }

        [Fact]
        public void Maps_namespace_flattened_names()
        {
            var compilation = Compile(("Components.cs",
                Usings + ContextDefinition + @"
namespace My.Game {
    public sealed class HealthComponent : Entitas.IComponent { public int value; }
}
"));

            var plan = RenameEngine.CreatePlan(compilation, "Health", "Hp");

            plan.NameMap.Should().ContainKey("MyGameHealth").WhoseValue.Should().Be("MyGameHp");
            plan.NameMap.Should().ContainKey("hasMyGameHealth").WhoseValue.Should().Be("hasMyGameHp");
            plan.NameMap.Should().ContainKey("HealthComponent").WhoseValue.Should().Be("HpComponent");
        }

        [Fact]
        public void Reports_ambiguous_component_names()
        {
            var compilation = Compile(("Components.cs",
                Usings + ContextDefinition + @"
namespace A { public sealed class HealthComponent : Entitas.IComponent { public int value; } }
namespace B { public sealed class HealthComponent : Entitas.IComponent { public int value; } }
"));

            var act = () => RenameEngine.CreatePlan(compilation, "Health", "Hp");

            act.Should().Throw<RenameException>()
                .WithMessage("*ambiguous*A.HealthComponent*B.HealthComponent*");
        }

        [Fact]
        public void Rejects_unknown_components()
        {
            var compilation = Compile(("Components.cs", Component));

            var act = () => RenameEngine.CreatePlan(compilation, "Mana", "Energy");

            act.Should().Throw<RenameException>().WithMessage("*no component named 'Mana'*");
        }

        [Fact]
        public void Refuses_to_rename_onto_an_existing_component()
        {
            var compilation = Compile(("Components.cs",
                Usings + ContextDefinition + @"
public sealed class HealthComponent : Entitas.IComponent { public int value; }
public sealed class ManaComponent : Entitas.IComponent { public int value; }
"));

            var act = () => RenameEngine.CreatePlan(compilation, "Health", "Mana");

            act.Should().Throw<RenameException>().WithMessage("*ManaComponent' already exists*");
        }

        [Fact]
        public void Renames_a_component_named_by_attribute()
        {
            var compilation = Compile(("Components.cs",
                Usings + ContextDefinition + @"
[ComponentName(""Health"")]
public sealed class HealthComponent : Entitas.IComponent { public int value; }
"));

            var plan = RenameEngine.CreatePlan(compilation, "Health", "Hp");
            var renamed = ApplyPlan(compilation, plan);

            plan.NameMap.Should().ContainKey("AddHealth").WhoseValue.Should().Be("AddHp");
            renamed["Components.cs"].Should().Contain("class HpComponent");
        }
    }
}
