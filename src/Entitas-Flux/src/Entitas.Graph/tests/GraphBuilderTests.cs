using System;
using System.Collections.Generic;
using System.Linq;
using Entitas.SourceGenerator;
using FluentAssertions;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Xunit;

namespace Entitas.Graph.Tests
{
    /// <summary>One test per kind of connection, on the Fixture/ project.</summary>
    public class GraphBuilderTests
    {
        static EcsGraph Graph => FixtureProject.Graph;

        static List<GraphEdge> Edges(string source, string relation, string target) =>
            Graph.Edges.Where(e => Label(e.Source) == source && e.Relation == relation && Label(e.Target) == target).ToList();

        static string Label(string id) => Graph.Node(id)?.Label ?? id;

        [Fact]
        public void The_fixture_compiles_with_its_generated_code()
        {
            CSharpGeneratorDriver.Create(new EntitasIncrementalGenerator())
                .RunGeneratorsAndUpdateCompilation(FixtureProject.Compile(), out var output, out _);
            output.GetDiagnostics().Where(d => d.Severity == DiagnosticSeverity.Error).Select(d => d.ToString()).Should().BeEmpty();
        }

        [Fact]
        public void Nodes_carry_their_ecs_facts()
        {
            var placed = Graph.Nodes.Single(n => n.Label == "FurniturePlacedEvent");
            placed.Kind.Should().Be(NodeKind.Component);
            placed.Role.Should().Be("event");
            placed.Contexts.Should().Equal("Game");
            placed.Flag.Should().BeFalse();

            Graph.Nodes.Single(n => n.Label == "Event").Role.Should().BeNull("the marker named like the suffix is not an event of its own");
            Graph.Nodes.Single(n => n.Label == "Selected").Attributes.Should().Contain("Watched");
            Graph.Nodes.Single(n => n.Label == "Tapped").Attributes.Should().Contain("Cleanup(RemoveComponent)");
            Graph.Nodes.Single(n => n.Label == "Pointer").Attributes.Should().Contain("Unique");
            Graph.Nodes.Single(n => n.Label == "Id").Attributes.Should().Contain("PrimaryEntityIndex");
            Graph.Nodes.Single(n => n.Label == "PlaceFurnitureByRequestSystem").Interfaces.Should().Equal("Execute");
            Graph.Nodes.Should().NotContain(n => n.Label == "SelectedChanged", "the [Watched] marker folds into its component");
        }

        [Fact]
        public void A_chained_call_on_a_returned_entity_creates()
        {
            // CreateEntity.Event().AddFurniturePlacedEvent(...) — the case a syntax-only graph misses.
            Edges("PlaceFurnitureByRequestSystem", Relation.Creates, "FurniturePlacedEvent")
                .Should().ContainSingle(e => e.Confidence == Confidence.Extracted && e.Via == "Add" && e.Entity == "new");
        }

        [Fact]
        public void Creating_through_a_factory_is_credited_to_the_caller()
        {
            Edges("PlaceFurnitureByRequestSystem", Relation.Creates, "Furniture")
                .Should().ContainSingle(e => e.Confidence == Confidence.Inferred && e.Through == "FurnitureFactory.CreateFurniture");
            Edges("PlaceFurnitureByRequestSystem", Relation.Creates, "Event")
                .Should().ContainSingle(e => e.Through == "CreateEntity.Event");
            Edges("FurnitureFactory", Relation.Creates, "Health").Should().ContainSingle(e => e.Confidence == Confidence.Extracted);
        }

        [Fact]
        public void Matchers_read_and_none_of_filters()
        {
            Edges("UnlockAchievementSystem", Relation.Reads, "FurniturePlacedEvent").Should().ContainSingle(e => e.Via == "AllOf");
            Edges("PlaceFurnitureByRequestSystem", Relation.Reads, "PlaceFurnitureRequest").Should().ContainSingle(e => e.Via == "Matcher");
            Edges("MarkEventsReadySystem", Relation.Filters, "Ready").Should().ContainSingle(e => e.Via == "NoneOf");
        }

        [Fact]
        public void Reading_through_a_query_interface_is_credited_to_the_caller()
        {
            Edges("CountFurnitureSystem", Relation.Reads, "Furniture")
                .Should().ContainSingle(e => e.Confidence == Confidence.Inferred && e.Through == "FurnitureQuery.Count");
            Edges("CountFurnitureSystem", Relation.Filters, "Destructed").Should().ContainSingle();
        }

        [Fact]
        public void Accessors_unique_components_and_entity_indices_read()
        {
            Edges("DamageSystem", Relation.Reads, "Health").Should().Contain(e => e.Via == "Get");
            Edges("DamageSystem", Relation.Reads, "Dead").Should().Contain(e => e.Via == "Flag");
            Edges("DamageSystem", Relation.Reads, "Id").Should().Contain(e => e.Via == "EntityIndex");
            Edges("RequestPlacementSystem", Relation.Reads, "Pointer").Select(e => e.Via).Should().Contain(new[] { "Has", "Get" });
            Edges("RequestPlacementSystem", Relation.Reads, "Pointer").Should().OnlyContain(e => e.Context == "Input");
        }

        [Fact]
        public void Flags_set_true_create_and_false_remove()
        {
            Edges("SelectSystem", Relation.Creates, "Selected").Should().ContainSingle(e => e.Via == "Flag" && e.Entity == "group");
            Edges("DeselectSystem", Relation.Removes, "Selected").Should().ContainSingle(e => e.Via == "Flag");
            Edges("DamageSystem", Relation.Removes, "Health").Select(e => e.Via).Should().BeEquivalentTo("Remove", "SafeRemove");
            Edges("DamageSystem", Relation.Creates, "Health").Should().ContainSingle(e => e.Via == "Replace");
        }

        [Fact]
        public void Watched_changes_are_reactions_to_the_component()
        {
            Edges("HighlightOnSelectedSystem", Relation.Reacts, "Selected").Should().ContainSingle(e => e.Via == "AllOf");
        }

        [Fact]
        public void Destroy_on_entities_of_a_group_destroys_what_the_group_requires()
        {
            Edges("DestroyEventsSystem", Relation.Destroys, "Event").Should().ContainSingle(e => e.Confidence == Confidence.Extracted);
            Edges("DestroyEventsSystem", Relation.Destroys, "Ready").Should().ContainSingle(e => e.Confidence == Confidence.Extracted);
        }

        [Fact]
        public void A_generic_destroyer_destroys_every_component_its_entities_were_created_with()
        {
            // FurniturePlacedEvent is created on CreateEntity.Event() (Event), Ready is added by
            // MarkEventsReadySystem, and DestroyEventsSystem destroys Event + Ready.
            var events = Edges("DestroyEventsSystem", Relation.Destroys, "FurniturePlacedEvent");
            events.Should().ContainSingle(e => e.Confidence == Confidence.Inferred);
            events[0].Through.Should().Contain("Event + FurniturePlacedEvent").And.Contain("Ready by MarkEventsReadySystem");

            // Requests: the handler marks them Destructed, a generic system destroys Destructed.
            Edges("DestroyDestructedSystem", Relation.Destroys, "PlaceFurnitureRequest")
                .Should().ContainSingle(e => e.Through!.Contains("Destructed by PlaceFurnitureByRequestSystem"));
        }

        [Fact]
        public void The_structure_finds_the_requests_and_events_the_names_say_except_one_nobody_handles()
        {
            // By name and by structure alike; OrphanRequest is never read, so never consumed: only
            // its name says it is a request. Event (the marker) and Destructed carry and doom, and
            // are no messages; Ready is no message because a Ready entity alone lives on.
            Graph.Nodes.Where(n => n.Role != null && n.Role != n.Message).Select(n => n.Label).Should().Equal("OrphanRequest");

            // Till has no suffix, and is one all the same: OpenShopSystem hands it over, and
            // SettleTillSystem turns it into an event and takes it off.
            var till = Graph.Nodes.Where(n => n.Message != null && n.Role == null).Should().ContainSingle().Subject;
            till.Label.Should().Be("Till");
            till.ConsumedBy.Select(Label).Should().Equal("SettleTillSystem");
            till.ConsumedByRemoval.Should().BeTrue();
            Graph.Nodes.Single(n => n.Label == "PlaceFurnitureRequest").ConsumedBy.Select(Label).Should().Equal("PlaceFurnitureByRequestSystem");
            Graph.Nodes.Single(n => n.Label == "FurniturePlacedEvent").CarriedBy.Select(Label).Should().Equal("Event");
            foreach (var plumbing in new[] { "Event", "Ready", "Destructed", "JustSpawned", "ShopItem" })
                Graph.Nodes.Single(n => n.Label == plumbing).Message.Should().BeNull(plumbing + " is no message");
        }

        [Fact]
        public void Generated_cleanup_systems_are_part_of_the_graph()
        {
            var cleanup = Graph.Nodes.Single(n => n.Label == "RemoveTappedGameSystem");
            cleanup.Generated.Should().BeTrue();
            Edges("RemoveTappedGameSystem", Relation.Removes, "Tapped").Should().NotBeEmpty();
        }

        [Fact]
        public void Features_register_systems_in_order_and_roots_are_found_without_names()
        {
            Edges("FurnitureFeature", Relation.Contains, "PlaceFurnitureByRequestSystem").Should().ContainSingle(e => e.Order == 2);
            Graph.Nodes.Where(n => n.Root).Select(n => n.Label).Should().BeEquivalentTo("GameplayFeature", "LateFeature", "CrateFeature", "DebugFeature");

            var placement = Graph.Nodes.Single(n => n.Label == "PlaceFurnitureByRequestSystem").Placements.Single();
            Label(placement.Root).Should().Be("GameplayFeature");
            placement.Order.Should().Be(3, "MarkEventsReadySystem, then FurnitureFeature's RequestPlacementSystem, then this");
            placement.Chain.Select(Label).Should().Equal("GameplayFeature", "FurnitureFeature");

            Graph.Nodes.Single(n => n.Label == "UnlockAchievementSystem").Placements.Single().Order.Should().Be(10);
            Graph.Nodes.Single(n => n.Label == "OrphanSystem").Placements.Should().BeEmpty();
        }

        [Fact]
        public void Writes_carry_every_condition_they_run_under_outermost_first()
        {
            var graph = Snippet(@"
                public static class Marks
                {
                    public static void Mark(GameEntity e) => e.isK = true;
                }

                public sealed class WriteSystem : IExecuteSystem
                {
                    readonly IGroup<GameEntity> _all;
                    readonly GameEntity _target;
                    readonly Kind _kind;
                    readonly int _amount;
                    readonly bool _ready;

                    public WriteSystem(GameContext game) => _all = game.GetGroup(GameMatcher.A);

                    public void Execute()
                    {
                        if (!_ready)
                            return;

                        foreach (GameEntity e in _all.GetEntities())
                        {
                            e.isB = true;
                            if (e.isC)
                                continue;
                            e.isD = true;
                            if (_amount < 0)
                                e.isE = true;
                            else
                                e.isF = true;
                            switch (_kind)
                            {
                                case Kind.One:
                                    e.isG = true;
                                    break;
                            }
                            GameEntity same = _amount > 9 ? e.ReplaceA(1) : e;
                            _target?.RemoveA();
                            try { } catch (InvalidOperationException) { e.isH = true; }
                            if (_amount > 100)
                            {
                                if (e.isB == false)
                                    e.isI = true;
                            }
                            if (_kind == Kind.Two && _amount < -1000 && _amount != 5)
                                e.isJ = true;
                            if (_amount == 7)
                                Marks.Mark(e);
                        }

                        if (_amount == 3)
                            return;
                        _target.isL = true;
                    }
                }

                public sealed class EitherSystem : IExecuteSystem
                {
                    readonly IGroup<GameEntity> _all;
                    readonly bool _flag;

                    public EitherSystem(GameContext game) => _all = game.GetGroup(GameMatcher.A);

                    public void Execute()
                    {
                        foreach (GameEntity e in _all.GetEntities())
                        {
                            if (_flag)
                                e.isC = true;
                            else
                            {
                                e.isC = true;
                                e.isD = true;
                            }
                        }
                    }
                }");

            string? Condition(string relation, string component, string system = "WriteSystem") => ConditionIn(graph, system, relation, component);

            // The guard Execute starts with says when the system runs, not which write happens.
            Condition(Relation.Creates, "B").Should().BeNull();
            Condition(Relation.Creates, "D").Should().Be("!e.isC", "a guard before it in its block");
            Condition(Relation.Creates, "E").Should().Be("!e.isC && _amount < 0", "the guard above it, then its own if");
            Condition(Relation.Creates, "F").Should().Be("!e.isC && !(_amount < 0)");
            Condition(Relation.Creates, "G").Should().Be("!e.isC && case One", "an enum member goes without its type");
            Condition(Relation.Creates, "A").Should().Be("!e.isC && _amount > 9");
            Condition(Relation.Removes, "A").Should().Be("!e.isC && _target != null");
            Condition(Relation.Creates, "H").Should().Be("!e.isC && caught InvalidOperationException");
            Condition(Relation.Creates, "I").Should().Be("!e.isC && _amount > 100 && !e.isB", "x == false reads as !x");
            Condition(Relation.Creates, "J").Should().Be("!e.isC && _kind == Two && _amount < -1000 && _amount != 5");
            Condition(Relation.Creates, "L").Should().Be("_amount != 3", "a guard after a loop tells the writes after it from those before");

            // Made in the if and in the else, it happens either way.
            Condition(Relation.Creates, "C", "EitherSystem").Should().BeNull();
            Condition(Relation.Creates, "D", "EitherSystem").Should().Be("!_flag");

            // Through a helper, the call's condition in the system, then the helper's own (none here).
            var mark = graph.Edges.Single(e => e.Target == "component:K" && e.Source.EndsWith("WriteSystem", StringComparison.Ordinal));
            mark.Confidence.Should().Be(Confidence.Inferred);
            mark.Condition.Should().Be("!e.isC && _amount == 7");
            mark.Called.Should().BeNull("the system calls the method the write is in");

            graph.Edges.Where(e => e.Relation is Relation.Reads or Relation.Filters).Should().OnlyContain(e => e.Condition == null, "only writes are conditional");
        }

        [Fact]
        public void A_long_condition_loses_its_tail()
        {
            var graph = Snippet(@"
                public sealed class LongSystem : IExecuteSystem
                {
                    readonly IGroup<GameEntity> _all;
                    readonly Kind _kind;
                    readonly int _alphaAmount, _betaAmount, _gammaAmount, _deltaAmount;
                    readonly bool _first, _second;

                    public LongSystem(GameContext game) => _all = game.GetGroup(GameMatcher.A);

                    public void Execute()
                    {
                        foreach (GameEntity e in _all.GetEntities())
                        {
                            e.isB = true;
                            if (_first)
                                continue;
                            if (_second)
                                continue;
                            if (_kind == Kind.Two)
                                e.isC = true;
                        }

                        if (_alphaAmount > 1000)
                            if (_betaAmount > 2000)
                                if (_gammaAmount > 3000)
                                    if (_deltaAmount > 4000)
                                        _all.GetEntities()[0].isD = true;

                        if (_alphaAmount > 1000)
                            if (_kind == Kind.Two && _betaAmount + _gammaAmount + _deltaAmount > 4000 + _alphaAmount)
                                _all.GetEntities()[0].isE = true;

                        if (_kind == Kind.Two && _alphaAmount > 1000 && _betaAmount > 2000 && _first)
                            _all.GetEntities()[0].isF = true;

                        if (_alphaAmount > 1000)
                            if (_betaAmount > 2000)
                                if (_gammaAmount > _deltaAmount)
                                    _all.GetEntities()[0].isG = true;

                        if (_first || _second)
                            if (_alphaAmount > 1)
                                _all.GetEntities()[0].isH = true;
                    }
                }");

            string? Condition(string component) => ConditionIn(graph, "LongSystem", Relation.Creates, component);

            Condition("C").Should().Be("!_first && !_second && _kind == Two", "guards in the order they are written, its own if last");
            Condition("F").Should().Be("_kind == Two && _alphaAmount > 1000 && _betaAmount > 2000 && _first");
            Condition("D").Should().Be("_alphaAmount > 1000 && _betaAmount > 2000 && _gammaAmount > 3000 && …",
                "then the innermost clauses go, the outermost stay");
            Condition("E").Should().Be("_alphaAmount > 1000 && _kind == Two && _betaAmount + _gammaAmount + _…",
                "a clause with enough room left is cut, not dropped");
            Condition("G").Should().Be("_alphaAmount > 1000 && _betaAmount > 2000 && _gammaAmount > _deltaAmount",
                "a last clause running over by less than ' && …' is kept whole");
            Condition("H").Should().Be("(_first || _second) && _alphaAmount > 1", "an || keeps its meaning among the &&s");
        }

        [Fact]
        public void A_private_method_called_only_under_a_condition_writes_under_it()
        {
            var graph = Snippet(@"
                public static class Marks
                {
                    public static void Mark(GameEntity e) => e.isK = true;
                }

                public sealed class PrivateSystem : IExecuteSystem
                {
                    readonly GameEntity _e;
                    readonly int _amount;
                    readonly bool _ready, _flag;

                    public void Execute()
                    {
                        if (_amount > 0)
                        {
                            if (_ready)
                                Pay();
                        }

                        Mixed();
                        if (_flag)
                            Mixed();

                        if (_flag)
                            Either(1);
                        else
                            Either(2);

                        if (_amount == 5)
                            Handed();
                        Action later = Handed;

                        if (_amount == 6)
                            Public();
                    }

                    void Pay()
                    {
                        _e.isB = true;
                        Nested();
                        Marks.Mark(_e);
                    }

                    void Nested()
                    {
                        if (_amount < 10)
                            _e.isC = true;
                    }

                    void Mixed() => _e.isD = true;
                    void Either(int value) => _e.isE = true;
                    void Handed() => _e.isF = true;
                    public void Public() => _e.isG = true;
                }");

            string? Condition(string component) => ConditionIn(graph, "PrivateSystem", Relation.Creates, component);

            Condition("B").Should().Be("_amount > 0 && _ready", "Execute calls Pay only there");
            Condition("C").Should().Be("_amount > 0 && _ready && _amount < 10", "nested private calls compose, the write's own if last");
            Condition("K").Should().Be("_amount > 0 && _ready", "a factory called from Pay is reached under the same condition");
            Condition("D").Should().BeNull("one of the calls to Mixed is unconditional");
            Condition("E").Should().BeNull("Either is called in the if and in its else");
            Condition("F").Should().BeNull("Handed is also handed out as a delegate, which may run it anywhere");
            Condition("G").Should().BeNull("a public method can be called from elsewhere");
        }

        [Fact]
        public void A_lambda_passed_with_a_when_argument_runs_under_it()
        {
            var graph = Snippet(@"
                public static class Functional
                {
                    public static T With<T>(this T self, Action<T> apply, bool when) { if (when) apply(self); return self; }
                    public static T Also<T>(this T self, Action<T> apply, bool force) { apply(self); return self; }
                    public static T If<T>(this T self, bool condition, Action<T> apply) { if (condition) apply(self); return self; }
                }

                public sealed class WhenSystem : IExecuteSystem
                {
                    readonly GameEntity _e;
                    readonly int _amount;
                    readonly bool _flag;

                    public void Execute()
                    {
                        _e.With(x => x.isB = true, when: _flag);
                        _e.With(x => x.isC = true, _amount > 3);
                        if (_amount < 0)
                            _e.With(x => x.isD = true, when: !_flag);
                        _e.Also(x => x.isE = true, force: _flag);
                        _e.With(x => x.isF = true, when: true);
                        _e.If(_flag, x => x.isG = true);
                    }
                }");

            string? Condition(string component) => ConditionIn(graph, "WhenSystem", Relation.Creates, component);

            Condition("B").Should().Be("_flag");
            Condition("C").Should().Be("_amount > 3", "the parameter is found by position too");
            Condition("D").Should().Be("_amount < 0 && !_flag", "inside the conditions around the call");
            Condition("E").Should().BeNull("force is no condition's name");
            Condition("F").Should().BeNull("a constant says nothing");
            Condition("G").Should().Be("_flag", "wherever the condition stands among the arguments");
        }

        [Fact]
        public void A_helper_s_body_says_which_argument_decides_whatever_its_parameters_are_called()
        {
            var graph = Snippet(@"
                public static class Helpers
                {
                    public static readonly List<Action> Later = new List<Action>();

                    public static T Gate<T>(this T self, Action<T> apply, bool open) { if (open) apply(self); return self; }
                    public static T Skip<T>(this T self, Action<T> apply, bool skip) { if (skip) return self; apply(self); return self; }
                    public static T Maybe<T>(this T self, Action<T> apply, bool when) { apply?.Invoke(self); return self; }
                    public static T Both<T>(this T self, Action<T> apply, bool a, bool b) { if (a && !b) apply.Invoke(self); return self; }
                    public static T Keep<T>(this T self, Action<T> apply, bool when) { if (when) Later.Add(() => apply(self)); return self; }
                    public static T Pick<T>(this T self, Func<T, T> apply, bool on) => on ? apply(self) : self;
                }

                public sealed class HelperSystem : IExecuteSystem
                {
                    readonly GameEntity _e;
                    readonly bool _flag, _other;

                    public void Execute()
                    {
                        _e.Gate(x => x.isB = true, _flag);
                        _e.Skip(x => x.isC = true, skip: _flag);
                        _e.Maybe(x => x.isD = true, when: _flag);
                        _e.Both(x => x.isE = true, _flag, _other);
                        _e.Keep(x => x.isF = true, when: _flag);
                        _e.Pick(x => { x.isG = true; return x; }, _flag);
                    }
                }");

            string? Condition(string component) => ConditionIn(graph, "HelperSystem", Relation.Creates, component);

            Condition("B").Should().Be("_flag", "Gate runs it if (open)");
            Condition("C").Should().Be("!_flag", "Skip returns early if (skip)");
            Condition("D").Should().BeNull("Maybe runs it whatever its `when` says");
            Condition("E").Should().Be("_flag && !_other", "if (a && !b): both arguments decide, in their order");
            Condition("F").Should().BeNull("Keep hands it to a list: when it runs is not known");
            Condition("G").Should().Be("_flag", "on ? apply(self) : self");
        }

        [Fact]
        public void A_helper_from_a_dll_is_known_by_its_parameter_names_only()
        {
            var library = CSharpCompilation.Create("Helpers", new[]
                {
                    CSharpSyntaxTree.ParseText(@"
                        using System;
                        public static class DllHelpers
                        {
                            public static T With<T>(this T self, Action<T> apply, bool when) { if (when) apply(self); return self; }
                            public static T Gate<T>(this T self, Action<T> apply, bool open) { if (open) apply(self); return self; }
                        }")
                },
                FixtureProject.References(), new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
            using var image = new System.IO.MemoryStream();
            library.Emit(image).Success.Should().BeTrue();

            var graph = Snippet(@"
                public sealed class DllSystem : IExecuteSystem
                {
                    readonly GameEntity _e;
                    readonly bool _flag;

                    public void Execute()
                    {
                        _e.With(x => x.isB = true, when: _flag);
                        _e.Gate(x => x.isC = true, open: _flag);
                    }
                }", MetadataReference.CreateFromImage(image.ToArray()));

            ConditionIn(graph, "DllSystem", Relation.Creates, "B").Should().Be("_flag", "`when` is one of the names a condition goes by");
            ConditionIn(graph, "DllSystem", Relation.Creates, "C").Should().BeNull("no body to read, and `open` is no such name");
        }

        [Fact]
        public void An_enum_member_keeps_its_type_only_where_its_name_would_be_ambiguous()
        {
            var graph = Snippet(@"
                public enum Shape { One, Round }

                public sealed class EnumSystem : IExecuteSystem
                {
                    readonly GameEntity _e;
                    readonly Kind _kind;
                    readonly Shape _shape;
                    readonly int Two;

                    public void Execute()
                    {
                        if (_kind == Kind.One && _shape == Shape.Round)
                            _e.isB = true;
                        if (_kind == Kind.One && _shape == Shape.One)
                            _e.isC = true;
                        if (_kind == Kind.Two && Two > 0)
                            _e.isD = true;
                    }
                }");

            string? Condition(string component) => ConditionIn(graph, "EnumSystem", Relation.Creates, component);

            Condition("B").Should().Be("_kind == One && _shape == Round");
            Condition("C").Should().Be("_kind == Kind.One && _shape == Shape.One", "two enums have a member One");
            Condition("D").Should().Be("_kind == Kind.Two && Two > 0", "a field is called Two");
        }

        const string Yard = @"
            public enum Hull { Steel, Wood, Glass }

            public static class Yard
            {
                public static void Build(GameEntity e, Kind kind)
                {
                    switch (kind)
                    {
                        case Kind.One:
                            e.isB = true;
                            break;
                        case Kind.Two:
                            e.isC = true;
                            break;
                    }
                }

                public static bool Pick(GameEntity e, Kind kind) => kind switch
                {
                    Kind.One => e.isD = true,
                    _ => e.isE = true
                };

                public static void Check(GameEntity e, Kind kind)
                {
                    if (kind == Kind.Two)
                        e.isF = true;
                    if (kind != Kind.Two)
                        e.isG = true;
                }

                public static void Paint(GameEntity e, Hull hull)
                {
                    switch (hull)
                    {
                        case Hull.Wood or Hull.Glass:
                            e.isH = true;
                            break;
                    }
                }

                public static void Guard(GameEntity e, Kind kind)
                {
                    switch (kind)
                    {
                        case Kind.One when e.isK:
                            e.isI = true;
                            break;
                    }
                }
            }";

        [Fact]
        public void A_constant_argument_decides_the_helper_s_switch_arm_and_if()
        {
            var graph = Snippet(Yard + @"
                public sealed class OneSystem : IExecuteSystem
                {
                    readonly GameEntity _e;

                    public void Execute()
                    {
                        Yard.Build(_e, Kind.One);
                        Yard.Pick(_e, Kind.One);
                        Yard.Check(_e, Kind.One);
                        Yard.Paint(_e, Hull.Steel);
                        Yard.Guard(_e, Kind.One);
                    }
                }

                public sealed class TwoSystem : IExecuteSystem
                {
                    readonly GameEntity _e;
                    readonly Kind _kind;

                    public void Execute()
                    {
                        Yard.Build(_e, Kind.Two);
                        Yard.Pick(_e, Kind.Two);
                        Yard.Paint(_e, Hull.Glass);
                        Yard.Guard(_e, Kind.Two);
                        Yard.Check(_e, _kind);
                    }
                }");

            string? Condition(string system, string component) => ConditionIn(graph, system, Relation.Creates, component);
            bool Creates(string system, string component) => CreatesIn(graph, system, component);

            // switch: the case passed, unconditionally; the other never.
            Condition("OneSystem", "B").Should().BeNull();
            Creates("OneSystem", "C").Should().BeFalse("Build is called with Kind.One");
            Condition("TwoSystem", "C").Should().BeNull();
            Creates("TwoSystem", "B").Should().BeFalse();

            // A switch expression: arms in order, the discard taking what the ones before it leave.
            Condition("OneSystem", "D").Should().BeNull();
            Creates("OneSystem", "E").Should().BeFalse("the first arm takes Kind.One");
            Condition("TwoSystem", "E").Should().BeNull();
            Creates("TwoSystem", "D").Should().BeFalse();

            // ==, != and `or` patterns.
            Creates("OneSystem", "F").Should().BeFalse();
            Condition("OneSystem", "G").Should().BeNull();
            Creates("OneSystem", "H").Should().BeFalse("Steel is neither Wood nor Glass");
            Condition("TwoSystem", "H").Should().BeNull();

            // `when` stays the condition once its case matches.
            Condition("OneSystem", "I").Should().Be("e.isK");
            Creates("TwoSystem", "I").Should().BeFalse();

            // A value that is no constant decides nothing; the helper's own edges keep every clause.
            Condition("TwoSystem", "F").Should().Be("kind == Two");
            Condition("TwoSystem", "G").Should().Be("kind != Two");
            Condition("Yard", "B").Should().Be("case One");
            Condition("Yard", "E").Should().Be("default");
            Condition("Yard", "H").Should().Be("case Wood or Glass");
            Condition("Yard", "I").Should().Be("case One when e.isK");
        }

        [Fact]
        public void Constants_reach_through_helpers_objects_and_results_but_not_past_a_reassignment()
        {
            var graph = Snippet(@"
                public static class Outer
                {
                    public static void Run(GameEntity e, Kind kind) => Inner.Apply(e, kind);
                    public static void Walk(GameEntity e, Kind kind) => Inner.Apply(e, kind);
                }

                public static class Inner
                {
                    public static void Apply(GameEntity e, Kind kind)
                    {
                        if (kind == Kind.Two)
                            e.isB = true;
                    }

                    public static void Reassigned(GameEntity e, Kind kind, bool swap)
                    {
                        if (swap)
                            kind = Kind.Two;
                        if (kind == Kind.Two)
                            e.isC = true;
                    }

                    public static void Named(GameEntity e, Kind kind)
                    {
                        bool isTwo = kind == Kind.Two;
                        if (isTwo)
                            e.isG = true;
                    }
                }

                public class Order
                {
                    public virtual Kind Level { get; }
                    public Order(Kind level) => Level = level;
                }

                public sealed class Rush : Order
                {
                    public Rush() : base(Kind.One) { }
                    public override Kind Level => Kind.Two;
                }

                public sealed class Spec
                {
                    public readonly Kind Kind;
                    public Kind Mode;
                    public string Label { get; set; }

                    public Spec(Kind kind) => Kind = kind;

                    public void Swap() => Mode = Kind.Two;
                }

                public sealed class Specs
                {
                    public Spec Make(Kind kind) => new Spec(kind);
                }

                public static class Plant
                {
                    public static void Build(GameEntity e, Spec spec)
                    {
                        switch (spec.Kind)
                        {
                            case Kind.Two:
                                e.isD = true;
                                break;
                        }
                        if (spec.Mode == Kind.Two)
                            e.isE = true;
                        if (spec.Label == ""gate"")
                            e.isF = true;
                    }

                    public static void Ship(GameEntity e, Order order)
                    {
                        if (order.Level == Kind.Two)
                            e.isH = true;
                    }
                }

                public sealed class ChainSystem : IExecuteSystem
                {
                    readonly GameEntity _e;
                    readonly Specs _specs;
                    readonly bool _flag;

                    public void Execute()
                    {
                        Outer.Run(_e, Kind.One);
                        Inner.Reassigned(_e, Kind.One, _flag);
                        var spec = _specs.Make(Kind.One);
                        Plant.Build(_e, spec);
                        Plant.Build(_e, new Spec(Kind.One) { Mode = Kind.One, Label = ""gate"" });
                        Inner.Named(_e, Kind.One);
                        Plant.Ship(_e, new Rush());
                    }
                }

                public sealed class MixedSystem : IExecuteSystem
                {
                    readonly GameEntity _e;
                    readonly int _amount;

                    public void Execute()
                    {
                        if (_amount > 0)
                            Outer.Run(_e, Kind.One);
                        if (_amount < 0)
                            Outer.Walk(_e, Kind.Two);
                    }
                }");

            // Through a helper passing its parameter on.
            CreatesIn(graph, "ChainSystem", "B").Should().BeFalse("Run passes Kind.One on to Apply");

            // A parameter assigned in the helper is not what the call passed.
            ConditionIn(graph, "ChainSystem", Relation.Creates, "C").Should().Be("kind == Two");

            // In an object: built by a method's result, a constructor, an initializer.
            CreatesIn(graph, "ChainSystem", "D").Should().BeFalse("both specs are built with Kind.One, which nothing changes");
            ConditionIn(graph, "ChainSystem", Relation.Creates, "E").Should().Be("spec.Mode == Two", "Swap writes Mode after the spec is built");
            ConditionIn(graph, "ChainSystem", Relation.Creates, "F").Should().BeNull(
                "the second spec is built with the label tested, so one way there is unconditional");

            // A local holding a test of the parameter is that test.
            CreatesIn(graph, "ChainSystem", "G").Should().BeFalse("isTwo is kind == Kind.Two, and kind is Kind.One");

            // The constructor sets Level to One, but Rush overrides it: a virtual property says nothing.
            ConditionIn(graph, "ChainSystem", Relation.Creates, "H").Should().Be("order.Level == Two");

            // Of two ways there only the one the constants allow is left, with its condition and its call.
            var mixed = graph.Edges.Single(e => e.Source.EndsWith(":MixedSystem", StringComparison.Ordinal) && e.Target == "component:B");
            mixed.Condition.Should().Be("_amount < 0");
            mixed.Through.Should().Be("Inner.Apply");
            mixed.Called.Should().Be("Outer.Walk");
        }

        [Fact]
        public void Literals_consts_and_nameof_are_constants_too()
        {
            var graph = Snippet(@"
                public sealed class Gate { }

                public static class Codes
                {
                    public const int Big = 7;

                    public static void ByNumber(GameEntity e, int level)
                    {
                        switch (level)
                        {
                            case 1: e.isB = true; break;
                            case Big: e.isC = true; break;
                        }
                    }

                    public static void ByName(GameEntity e, string name)
                    {
                        if (name == ""Gate"")
                            e.isD = true;
                    }

                    public static void ByFlag(GameEntity e, bool on)
                    {
                        if (on)
                            e.isE = true;
                        else
                            e.isF = true;
                    }

                    public static void ByTag(GameEntity e, Tag tag, Gate gate)
                    {
                        if (tag == null)
                            e.isG = true;
                        if (gate == null)
                            e.isH = true;
                    }
                }

                // Like a UnityEngine.Object: == says what it likes, even of a new object and null.
                public sealed class Tag
                {
                    public static bool operator ==(Tag a, Tag b) => true;
                    public static bool operator !=(Tag a, Tag b) => false;
                    public override bool Equals(object other) => true;
                    public override int GetHashCode() => 0;
                }

                public sealed class CodeSystem : IExecuteSystem
                {
                    readonly GameEntity _e;

                    public void Execute()
                    {
                        Codes.ByNumber(_e, Codes.Big);
                        Codes.ByName(_e, nameof(Gate));
                        Codes.ByFlag(_e, false);
                        Codes.ByTag(_e, new Tag(), new Gate());
                    }
                }");

            CreatesIn(graph, "CodeSystem", "B").Should().BeFalse();
            ConditionIn(graph, "CodeSystem", Relation.Creates, "C").Should().BeNull("a const");
            ConditionIn(graph, "CodeSystem", Relation.Creates, "D").Should().BeNull("nameof is a constant string");
            CreatesIn(graph, "CodeSystem", "E").Should().BeFalse("false never takes the if");
            ConditionIn(graph, "CodeSystem", Relation.Creates, "F").Should().BeNull("and always takes its else");

            // A new object is never null — unless == is the type's own, which may say anything.
            ConditionIn(graph, "CodeSystem", Relation.Creates, "G").Should().Be("tag == null");
            CreatesIn(graph, "CodeSystem", "H").Should().BeFalse();
        }

        static bool CreatesIn(EcsGraph graph, string system, string component) =>
            graph.Edges.Any(e => e.Source.EndsWith(":" + system, StringComparison.Ordinal) && e.Relation == Relation.Creates && e.Target == "component:" + component);

        const string Crates = @"
            [Game, Watched] public sealed class W : IComponent { public int Value; }

            // B is put only on crates, C only on barrels, as they are made.
            public static class Make
            {
                public static GameContext Game;
                public static GameEntity Crate() { GameEntity e = Game.CreateEntity(); e.isB = true; return e; }
                public static GameEntity Barrel() { GameEntity e = Game.CreateEntity(); e.isC = true; return e; }
            }

            public static class Tweak
            {
                public static GameEntity Set(this GameEntity e) { e.AddW(1); return e; }
                public static GameEntity Reset(this GameEntity e) { e.ReplaceW(0); return e; }
            }

            public sealed class OnBarrelSystem : IExecuteSystem
            {
                readonly IGroup<GameEntity> _changed;
                public OnBarrelSystem(GameContext game) => _changed = game.GetGroup(GameMatcher.AllOf(GameMatcher.C, GameMatcher.WChanged));
                public void Execute() { }
            }

            // D is given to existing crates as well: a crate may have it by the time W changes.
            public sealed class OnLabelledSystem : IExecuteSystem
            {
                readonly IGroup<GameEntity> _changed;
                public OnLabelledSystem(GameContext game) => _changed = game.GetGroup(GameMatcher.AllOf(GameMatcher.D, GameMatcher.WChanged));
                public void Execute() { }
            }

            public sealed class LabelSystem : IExecuteSystem
            {
                readonly IGroup<GameEntity> _crates;
                public LabelSystem(GameContext game) => _crates = game.GetGroup(GameMatcher.B);
                public void Execute() { foreach (GameEntity crate in _crates.GetEntities()) crate.isD = true; }
            }

            public sealed class MakeCratesSystem : IExecuteSystem
            {
                public void Execute() => Make.Crate().Set();
            }

            // Reset is also handed out as a delegate, so it may run on anything.
            public sealed class ResetCratesSystem : IExecuteSystem
            {
                public Func<GameEntity, GameEntity> Later = Tweak.Reset;
                public void Execute() => Make.Crate().Reset();
            }";

        [Fact]
        public void A_reaction_is_unseen_only_where_the_entity_certainly_never_gets_what_its_group_needs()
        {
            var graph = Snippet(Crates);
            IEnumerable<string> UnseenBy(string system) => graph.Edges
                .Single(e => e.Source.EndsWith(":" + system, StringComparison.Ordinal) && e.Target == "component:W")
                .UnseenBy.Select(u => $"{u.System.Split(':')[1]} needs {u.Needs.Split(':')[1]}");

            UnseenBy("MakeCratesSystem").Should().Equal(new[] { "OnBarrelSystem needs C" },
                "a crate is made with B alone, and C is only put on barrels as they are made; D can come later");
            UnseenBy("ResetCratesSystem").Should().BeEmpty("Reset is handed out as a delegate: what it runs on is not known");

            // One component added where the code does not say which, and nothing is certain any more.
            var untraced = Snippet(Crates + @"
                public sealed class CopySystem : IExecuteSystem
                {
                    GameEntity _from, _to;
                    int _index;
                    public void Execute() => _to.AddComponent(_index, _from.GetComponent(_index));
                }");
            untraced.Edges.Should().OnlyContain(e => e.UnseenBy.Count == 0);
        }

        static string? ConditionIn(EcsGraph graph, string system, string relation, string component) =>
            graph.Edges.Single(e => e.Source.EndsWith(":" + system, StringComparison.Ordinal) && e.Relation == relation && e.Target == "component:" + component).Condition;

        /// <summary>
        /// A one-file project with components A (data) and B…L (flags), an enum Kind, and
        /// <paramref name="code"/> — all in the global namespace, which keeps the generated names
        /// short (isB, not isSB) without any generation option.
        /// </summary>
        static EcsGraph Snippet(string code, params MetadataReference[] references)
        {
            var source = @"
                using System;
                using System.Collections.Generic;
                using Entitas;
                using Entitas.CodeGeneration.Attributes;

                [assembly: ContextDefinition(""Game"")]

                [Game] public sealed class A : IComponent { public int Value; }
                " + string.Join("\n", "BCDEFGHIJKL".Select(c => $"[Game] public sealed class {c} : IComponent {{ }}")) + @"
                public enum Kind { One, Two }
                " + code;
            var compilation = CSharpCompilation.Create("Snippet", new[] { CSharpSyntaxTree.ParseText(source, path: "/snippet/Code.cs") },
                FixtureProject.References().Concat(references), new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
            CSharpGeneratorDriver.Create(new EntitasIncrementalGenerator())
                .RunGeneratorsAndUpdateCompilation(compilation, out var generated, out _);
            generated.GetDiagnostics().Where(d => d.Severity == DiagnosticSeverity.Error).Select(d => d.ToString()).Should().BeEmpty();
            return Analysis.GraphBuilder.Build(compilation, "/snippet", new Analysis.GraphOptions());
        }

        [Fact]
        public void File_mode_gives_the_same_graph()
        {
            var inMemory = FixtureProject.Graph;
            var onDisk = FixtureProject.Build(filesMode: true);

            Signature(onDisk).Should().BeEquivalentTo(Signature(inMemory));
        }

        static IEnumerable<string> Signature(EcsGraph graph) =>
            graph.Edges.Select(e => $"{e.Source} {e.Relation} {e.Target} {e.Via} {e.Confidence} {e.Through} {e.Called} {e.Entity} {e.Order} {e.OnlyWhile} {e.Condition}")
                .Concat(graph.Nodes.Select(n => $"{n.Id} {n.Kind} {n.Root} {string.Join(",", n.Placements.Select(p => p.Root + p.Order))} {string.Join(",", n.TransientRemovedBy)}"));
    }
}
