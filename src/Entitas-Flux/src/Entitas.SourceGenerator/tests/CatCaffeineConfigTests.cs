using System;
using System.Collections.Generic;
using System.Reflection;
using FluentAssertions;
using Xunit;

namespace Entitas.SourceGenerator.Tests
{
    /// <summary>
    /// Every generation feature Cat Caffeine (a ~2000-file production project) relies on,
    /// together in one assembly and with its exact configuration — two contexts, the atomic
    /// entity API, namespaces ignored, debug hooks — compiled and run in both output modes.
    /// Features interact (an atomic [Watched] component, an [Event] in a namespace, a
    /// [Unique] in the second context), and that interaction is what a single-feature test
    /// does not see.
    /// </summary>
    public class CatCaffeineConfigTests
    {
        const string Source = @"
using Entitas;
using Entitas.CodeGeneration.Attributes;

[assembly: ContextDefinition(""Game"")]
[assembly: ContextDefinition(""Input"")]
[assembly: EntitasGeneration(EntityApi = EntityApiStyle.Atomic, IgnoreNamespaces = true, DebugHooks = true)]

namespace Code.Gameplay
{
    [Game, Watched] public sealed class Score : IComponent { public int Value; }
    [Game, Event(EventTarget.Any)] public sealed class Coins : IComponent { public int Value; }
    [Game, Cleanup(CleanupMode.RemoveComponent)] public sealed class Damaged : IComponent { }
    [Game] public sealed class Id : IComponent { [PrimaryEntityIndex] public int Value; }
    [Game] public sealed class Team : IComponent { [EntityIndex] public int Value; }
    [Game, FlagPrefix(""flag"")] public sealed class Selected : IComponent { }
    [Input, Unique] public sealed class PointerPosition : IComponent { public int Value; }

    public sealed class CoinsRecorder : IAnyCoinsListener
    {
        public readonly System.Collections.Generic.List<int> Values = new System.Collections.Generic.List<int>();
        public void OnAnyCoins(GameEntity entity, int value) => Values.Add(value);
    }
}
";

        [Theory, MemberData(nameof(OutputModes.Both), MemberType = typeof(OutputModes))]
        public void Every_feature_works_together(Output output)
        {
            var assembly = OutputModes.Load(OutputModes.Build(nameof(CatCaffeineConfigTests), Source, output));
            dynamic contexts = Activator.CreateInstance(assembly.GetType("Contexts")!)!;
            dynamic game = contexts.game;

            // [Watched] on an atomic component, and its generated cleanup.
            dynamic player = game.CreateEntity();
            player.AddScore(1);
            ((bool)player.isScoreChanged).Should().BeTrue();
            Instance(assembly, "GameWatchedCleanupSystems", contexts).Cleanup();
            ((bool)player.isScoreChanged).Should().BeFalse();

            // [Event(Any)]: the generated event systems call the listener. They are reactive,
            // so — as in a game — they exist before the change they report.
            dynamic eventSystems = Instance(assembly, "GameEventSystems", contexts);
            dynamic recorder = Activator.CreateInstance(assembly.GetType("Code.Gameplay.CoinsRecorder")!)!;
            dynamic listener = game.CreateEntity();
            listener.AddAnyCoinsListener(recorder);
            player.AddCoins(5);
            eventSystems.Execute();
            ((List<int>)recorder.Values).Should().Equal(5);

            // [Cleanup]: removed by the generated cleanup systems.
            player.isDamaged = true;
            Instance(assembly, "GameCleanupSystems", contexts).Cleanup();
            ((bool)player.isDamaged).Should().BeFalse();

            // [PrimaryEntityIndex] and [EntityIndex].
            player.AddId(7);
            player.AddTeam(2);
            dynamic teammate = game.CreateEntity();
            teammate.AddTeam(2);
            ((object)Lookup(assembly, "GetEntityWithId", (object)game, 7)).Should().BeSameAs((object)player);
            Count(Lookup(assembly, "GetEntitiesWithTeam", (object)game, 2)).Should().Be(2);

            // [FlagPrefix].
            player.flagSelected = true;
            ((bool)player.flagSelected).Should().BeTrue();

            // [Unique] in the second context.
            dynamic input = contexts.input;
            input.SetPointerPosition(3);
            ((bool)input.hasPointerPosition).Should().BeTrue();

            // DebugHooks: every Replace is reported, including the atomic in-place one.
            var replaced = new List<int>();
            EntitasDebugHooks.OnReplace = (_, index, _) => replaced.Add(index);
            try
            {
                player.ReplaceScore(2);
                replaced.Should().ContainSingle();
            }
            finally
            {
                EntitasDebugHooks.OnReplace = null;
            }
        }

        static dynamic Instance(Assembly assembly, string typeName, object contexts) =>
            Activator.CreateInstance(assembly.GetType(typeName)!, contexts)!;

        /// <summary>Entity-index lookups are generated as extension methods, which `dynamic` cannot call.</summary>
        static object Lookup(Assembly assembly, string method, object context, int key) =>
            assembly.GetType("ContextsExtensions")!.GetMethod(method)!.Invoke(null, new[] { context, key })!;

        static int Count(object set)
        {
            var count = 0;
            foreach (var _ in (System.Collections.IEnumerable)set)
                count++;
            return count;
        }
    }
}
