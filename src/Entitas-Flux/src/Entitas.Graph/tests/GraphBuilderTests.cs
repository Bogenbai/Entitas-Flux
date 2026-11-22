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
            Graph.Nodes.Where(n => n.Root).Select(n => n.Label).Should().BeEquivalentTo("GameplayFeature", "LateFeature");

            var placement = Graph.Nodes.Single(n => n.Label == "PlaceFurnitureByRequestSystem").Placements.Single();
            Label(placement.Root).Should().Be("GameplayFeature");
            placement.Order.Should().Be(3, "MarkEventsReadySystem, then FurnitureFeature's RequestPlacementSystem, then this");
            placement.Chain.Select(Label).Should().Equal("GameplayFeature", "FurnitureFeature");

            Graph.Nodes.Single(n => n.Label == "UnlockAchievementSystem").Placements.Single().Order.Should().Be(10);
            Graph.Nodes.Single(n => n.Label == "OrphanSystem").Placements.Should().BeEmpty();
        }

        [Fact]
        public void File_mode_gives_the_same_graph()
        {
            var inMemory = FixtureProject.Graph;
            var onDisk = FixtureProject.Build(filesMode: true);

            Signature(onDisk).Should().BeEquivalentTo(Signature(inMemory));
        }

        static IEnumerable<string> Signature(EcsGraph graph) =>
            graph.Edges.Select(e => $"{e.Source} {e.Relation} {e.Target} {e.Via} {e.Confidence} {e.Through} {e.Entity} {e.Order}")
                .Concat(graph.Nodes.Select(n => $"{n.Id} {n.Kind} {n.Root} {string.Join(",", n.Placements.Select(p => p.Root + p.Order))}"));
    }
}
