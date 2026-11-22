using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using FluentAssertions;
using Xunit;

namespace Entitas.SourceGenerator.Tests
{
    /// <summary>
    /// Runs the generated code instead of comparing its text.
    ///
    /// Everything else in this suite proves the generator emits the expected
    /// characters; these tests compile the output into a real assembly, load it, and
    /// exercise the API the way a game would. That is the only way to catch a feature
    /// that generates perfectly and then does nothing at runtime.
    ///
    /// Every test runs twice: with the source generator, and with the code on disk as
    /// generated files (Output = GenerationOutput.Files) and the source generator standing
    /// down. Both must behave identically.
    /// </summary>
    public class FluxBehaviorTests
    {
        const string Attributes = "using Entitas.CodeGeneration.Attributes;\n";
        const string GameContext = "[assembly: ContextDefinition(\"Game\")]\n";
        const string Atomic = "[assembly: EntitasGeneration(EntityApi = EntityApiStyle.Atomic)]\n";

        // -- atomic entity API -------------------------------------------------

        [Theory, MemberData(nameof(OutputModes.Both), MemberType = typeof(OutputModes))]
        public void Atomic_api_exposes_the_single_value_directly(Output output)
        {
            dynamic entity = CreateEntity(
                nameof(Atomic_api_exposes_the_single_value_directly), output,
                Attributes + GameContext + Atomic + @"
public sealed class CurrentHealthComponent : Entitas.IComponent { public int Value; }
");

            entity.AddCurrentHealth(10);
            ((int)entity.CurrentHealth).Should().Be(10);

            entity.ReplaceCurrentHealth(7);
            ((int)entity.CurrentHealth).Should().Be(7);
            ((bool)entity.hasCurrentHealth).Should().BeTrue();
        }

        [Theory, MemberData(nameof(OutputModes.Both), MemberType = typeof(OutputModes))]
        public void Atomic_replace_adds_the_component_when_it_is_missing(Output output)
        {
            dynamic entity = CreateEntity(
                nameof(Atomic_replace_adds_the_component_when_it_is_missing), output,
                Attributes + GameContext + Atomic + @"
public sealed class CurrentHealthComponent : Entitas.IComponent { public int Value; }
");

            ((bool)entity.hasCurrentHealth).Should().BeFalse();

            entity.ReplaceCurrentHealth(3);

            ((bool)entity.hasCurrentHealth).Should().BeTrue("Replace on a missing component adds it");
            ((int)entity.CurrentHealth).Should().Be(3);
        }

        // -- safe removal ------------------------------------------------------

        [Theory, MemberData(nameof(OutputModes.Both), MemberType = typeof(OutputModes))]
        public void Safe_remove_removes_the_component_and_is_a_no_op_when_it_is_absent(Output output)
        {
            dynamic entity = CreateEntity(
                nameof(Safe_remove_removes_the_component_and_is_a_no_op_when_it_is_absent), output,
                Attributes + GameContext + @"
public sealed class HealthComponent : Entitas.IComponent { public int value; }
");

            entity.AddHealth(5);
            entity.SafeRemoveHealth();
            ((bool)entity.hasHealth).Should().BeFalse();

            // The point of SafeRemove: calling it again must not throw, where
            // RemoveHealth() would.
            Action removeAgain = () => entity.SafeRemoveHealth();
            removeAgain.Should().NotThrow();

            Action removeUnsafe = () => entity.RemoveHealth();
            removeUnsafe.Should().Throw<Exception>("plain Remove still throws — that is what SafeRemove exists for");
        }

        // -- [Watched] ---------------------------------------------------------

        [Theory, MemberData(nameof(OutputModes.Both), MemberType = typeof(OutputModes))]
        public void Watched_marks_the_entity_on_replace_and_the_generated_cleanup_clears_it(Output output)
        {
            var assembly = Build(
                nameof(Watched_marks_the_entity_on_replace_and_the_generated_cleanup_clears_it), output,
                Attributes + GameContext + Atomic + @"
[Watched]
public sealed class CurrentHealthComponent : Entitas.IComponent { public int Value; }
");

            dynamic contexts = Activator.CreateInstance(assembly.GetType("Contexts")!)!;
            dynamic entity = contexts.game.CreateEntity();

            entity.AddCurrentHealth(1);
            entity.ReplaceCurrentHealth(2);
            ((bool)entity.isCurrentHealthChanged).Should().BeTrue("a watched component marks the entity when it changes");

            // The generated cleanup feature is what makes the marker last exactly one frame.
            dynamic cleanup = Activator.CreateInstance(
                assembly.GetType("GameWatchedCleanupSystems")!, new object[] { contexts })!;
            cleanup.Cleanup();

            ((bool)entity.isCurrentHealthChanged).Should().BeFalse("the cleanup systems drop the marker");
        }

        [Theory, MemberData(nameof(OutputModes.Both), MemberType = typeof(OutputModes))]
        public void Watched_works_with_the_plain_entity_api_too(Output output)
        {
            // [Watched] was wired into the atomic entity API only, so with the DEFAULT
            // plain API the marker component and the cleanup systems were generated and
            // nothing ever set the flag — the feature silently did nothing.
            dynamic entity = CreateEntity(
                nameof(Watched_works_with_the_plain_entity_api_too), output,
                Attributes + GameContext + @"
[Watched]
public sealed class HealthComponent : Entitas.IComponent { public int value; }
");

            entity.AddHealth(1);
            ((bool)entity.isHealthChanged).Should().BeTrue("Add marks the entity");

            entity.ReplaceHealth(2);
            ((bool)entity.isHealthChanged).Should().BeTrue("Replace marks the entity");
        }

        [Theory, MemberData(nameof(OutputModes.Both), MemberType = typeof(OutputModes))]
        public void Watched_works_for_a_component_declared_in_a_namespace(Output output)
        {
            // The marker class is named from the flattened component name, but the data
            // model used to name it from the short one: every generated reference to the
            // marker pointed at a type that did not exist, and [Watched] inside a
            // namespace did not compile at all unless IgnoreNamespaces was on.
            dynamic entity = CreateEntity(
                nameof(Watched_works_for_a_component_declared_in_a_namespace), output,
                Attributes + GameContext + @"
namespace My.Game {
    [Watched]
    public sealed class HealthComponent : Entitas.IComponent { public int value; }
}
");

            entity.AddMyGameHealth(1);

            ((bool)entity.isMyGameHealthChanged).Should().BeTrue();
        }

        // -- debug hooks -------------------------------------------------------

        [Theory, MemberData(nameof(OutputModes.Both), MemberType = typeof(OutputModes))]
        public void Debug_hooks_report_the_mutation_that_happened(Output output)
        {
            var assembly = Build(
                nameof(Debug_hooks_report_the_mutation_that_happened), output,
                Attributes + GameContext +
                "[assembly: EntitasGeneration(DebugHooks = true)]\n" + @"
public sealed class HealthComponent : Entitas.IComponent { public int value; }
");

            var added = new List<(int Index, object Value)>();
            var replaced = new List<(int Index, object Value)>();
            Entitas.EntitasDebugHooks.OnAdd = (_, index, value) => added.Add((index, value));
            Entitas.EntitasDebugHooks.OnReplace = (_, index, value) => replaced.Add((index, value));

            try
            {
                dynamic contexts = Activator.CreateInstance(assembly.GetType("Contexts")!)!;
                dynamic entity = contexts.game.CreateEntity();

                entity.AddHealth(1);
                entity.ReplaceHealth(2);

                var healthIndex = (int)assembly.GetType("GameComponentsLookup")!
                    .GetField("Health", BindingFlags.Public | BindingFlags.Static)!
                    .GetValue(null)!;

                added.Should().ContainSingle().Which.Index.Should().Be(healthIndex);
                replaced.Should().ContainSingle().Which.Index.Should().Be(healthIndex);
            }
            finally
            {
                Entitas.EntitasDebugHooks.OnAdd = null;
                Entitas.EntitasDebugHooks.OnReplace = null;
            }
        }

        [Theory, MemberData(nameof(OutputModes.Both), MemberType = typeof(OutputModes))]
        public void Debug_hooks_are_absent_unless_enabled(Output output)
        {
            var calls = 0;
            Entitas.EntitasDebugHooks.OnAdd = (_, _, _) => calls++;

            try
            {
                dynamic entity = CreateEntity(
                    nameof(Debug_hooks_are_absent_unless_enabled), output,
                    Attributes + GameContext + @"
public sealed class HealthComponent : Entitas.IComponent { public int value; }
");
                entity.AddHealth(1);

                calls.Should().Be(0, "hooks must cost nothing when the flag is off");
            }
            finally
            {
                Entitas.EntitasDebugHooks.OnAdd = null;
            }
        }

        // -- groups and collectors through the generated API -------------------

        [Theory, MemberData(nameof(OutputModes.Both), MemberType = typeof(OutputModes))]
        public void Groups_and_collectors_follow_component_changes(Output output)
        {
            var assembly = Build(
                nameof(Groups_and_collectors_follow_component_changes), output,
                Attributes + GameContext + @"
public sealed class HealthComponent : Entitas.IComponent { public int value; }
public sealed class DeadComponent : Entitas.IComponent { }
");

            dynamic contexts = Activator.CreateInstance(assembly.GetType("Contexts")!)!;
            dynamic game = contexts.game;

            var matcherType = assembly.GetType("GameMatcher")!;
            dynamic healthMatcher = matcherType.GetProperty("Health")!.GetValue(null)!;
            dynamic group = game.GetGroup(healthMatcher);

            var entities = new List<dynamic>();
            for (var i = 0; i < 50; i++)
            {
                dynamic entity = game.CreateEntity();
                entity.AddHealth(i);
                entities.Add(entity);
            }

            ((int)group.count).Should().Be(50);

            // Remove from the middle: the group swaps entities into freed slots, and this
            // is where losing or duplicating one would show.
            for (var i = 0; i < 50; i += 2)
                entities[i].RemoveHealth();

            ((int)group.count).Should().Be(25);
            foreach (var (entity, index) in entities.Select((e, i) => (e, i)))
                ((bool)group.ContainsEntity(entity)).Should().Be(index % 2 != 0);

            // Entities come back from the pool with the same identity; the group must not
            // remember them from their previous life.
            game.DestroyAllEntities();
            ((int)group.count).Should().Be(0);

            dynamic revived = game.CreateEntity();
            revived.AddHealth(1);
            ((int)group.count).Should().Be(1);
            ((bool)group.ContainsEntity(revived)).Should().BeTrue();
        }

        // -- harness -----------------------------------------------------------

        static dynamic CreateEntity(string assemblyName, Output output, string source)
        {
            var assembly = Build(assemblyName, output, source);
            dynamic contexts = Activator.CreateInstance(assembly.GetType("Contexts")!)!;
            return contexts.game.CreateEntity();
        }

        /// <summary>Generates, compiles and loads the result so it can actually be run — in either output mode.</summary>
        static Assembly Build(string assemblyName, Output output, string source) =>
            OutputModes.Load(OutputModes.Build(assemblyName, source, output));
    }
}
