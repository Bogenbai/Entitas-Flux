using System;
using System.Collections.Generic;
using System.Linq;
using Entitas.Graph.Output;
using FluentAssertions;
using Xunit;

namespace Entitas.Graph.Tests
{
    /// <summary>
    /// Flags destroyed with their entity or set once on purpose, and reactions to a [Watched]
    /// change seen from the system that causes them — on the shop and crate parts of the
    /// Fixture/ project.
    /// </summary>
    public class FlagsAndWatchedTests
    {
        static EcsGraph Graph => FixtureProject.Graph;

        static readonly GraphQueries Queries = new GraphQueries(Graph);

        static List<GraphEdge> Edges(string source, string relation, string target) =>
            Graph.Edges.Where(e => Label(e.Source) == source && e.Relation == relation && Label(e.Target) == target).ToList();

        static string Label(string id) => Graph.Node(id)?.Label ?? id;

        // -- flags destroyed with their entity ---------------------------------------------------

        [Fact]
        public void A_flag_set_in_the_loop_that_marks_the_entity_for_a_destroyer_is_destroyed_with_it()
        {
            // StoreItemsSystem: isProcessingStoring = true; isDestructed = true — same entity, same loop.
            Edges("DestroyDestructedSystem", Relation.Destroys, "ProcessingStoring").Should().ContainSingle(e =>
                e.Confidence == Confidence.Inferred && e.Through == "marked Destructed by StoreItemsSystem together with ProcessingStoring");
        }

        [Fact]
        public void A_helper_marking_the_loop_entity_destroys_what_the_group_requires()
        {
            // ExpireOffersSystem: foreach over AllOf(ShopItem, TimeIsUp) → Expire(offer) → isDestructed = true.
            Edges("ExpireOffersSystem", Relation.Creates, "Destructed").Should().ContainSingle(e => e.Entity == "group",
                "the helper's parameter is the loop variable of its only caller");
            Edges("DestroyDestructedSystem", Relation.Destroys, "TimeIsUp").Should().ContainSingle(e =>
                e.Confidence == Confidence.Inferred && e.Through == "marked Destructed by ExpireOffersSystem, whose group requires TimeIsUp");
        }

        [Fact]
        public void Nothing_is_inferred_across_systems_and_what_the_shapes_gave_is_not_repeated()
        {
            // Unboxed and Receipted are set by other systems on other groups than the one marked Destructed.
            Edges("DestroyDestructedSystem", Relation.Destroys, "Unboxed").Should().BeEmpty();
            Edges("DestroyDestructedSystem", Relation.Destroys, "Receipted").Should().BeEmpty();

            // ShopItem is required by both marking loops, but its creation shape already says it.
            Edges("DestroyDestructedSystem", Relation.Destroys, "ShopItem").Should().ContainSingle(e => e.Through!.StartsWith("created with ShopItem + Unpaid"));
            Edges("DestroyDestructedSystem", Relation.Destroys, "PlaceFurnitureRequest").Should().ContainSingle();

            // MarkEventsReadySystem marks Ready on AllOf(Event): nothing but the destroyer's own requirements.
            Graph.Edges.Where(e => Label(e.Source) == "DestroyEventsSystem" && e.Through != null && e.Through.StartsWith("marked", StringComparison.Ordinal))
                .Should().BeEmpty();
        }

        [Fact]
        public void The_report_lists_destroyed_flags_nowhere_and_done_markers_apart()
        {
            var report = GraphReport.Build(Graph);
            var neverCleared = Section(report, "State flags that are never cleared");
            var setOnce = Section(report, "Flags set once on purpose?");

            neverCleared.Should().Contain("`Inspected`")
                .And.Contain("`Receipted`", "its setter filters it with NoneOf, but sets it in reaction to a [Watched] change, which comes again")
                .And.NotContain("`ProcessingStoring`").And.NotContain("`TimeIsUp`").And.NotContain("`Unboxed`");
            setOnce.Should().StartWith("### Flags set once on purpose? (the setter filters them out with NoneOf, or only sees entities while they carry a transient component) — 2")
                .And.Contain("- `Unboxed` set by `UnboxItemsSystem` — Components.cs:30")
                .And.NotContain("`TimeIsUp`", "it is guarded by NoneOf too, but destroyed with its entity");
        }

        // -- flags set only while a transient component is there -----------------------------------

        [Fact]
        public void A_component_one_system_takes_off_every_holder_is_transient()
        {
            string[] RemovedBy(string component) => Graph.Nodes.Single(n => n.Label == component).TransientRemovedBy.Select(Label).ToArray();

            RemovedBy("JustSpawned").Should().Equal(new[] { "UnmarkJustSpawnedSystem" },
                "its NoneOf skips only the crates the same loop marked SpawnSeen, which it took JustSpawned from");
            RemovedBy("Wrapped").Should().Equal(new[] { "UnwrapCratesSystem" }, "RemoveComponent(GameComponentsLookup.Wrapped) is a removal too");
            RemovedBy("Tapped").Should().Equal(new[] { "RemoveTappedGameSystem" }, "the generated [Cleanup] system has the same shape");

            RemovedBy("Fragile").Should().BeEmpty("PackFragileCratesSystem can continue before the removal: some crates keep it");
            RemovedBy("Stacked").Should().BeEmpty("UnstackCratesSystem's NoneOf spares wrapped crates, and it does not wrap them itself");
            RemovedBy("Unpaid").Should().BeEmpty("CommitCartSystem's group requires ShopItem too: not every holder");
            RemovedBy("SpawnSeen").Should().BeEmpty();
        }

        [Fact]
        public void A_flag_set_in_a_loop_over_a_transient_component_records_it_on_the_edge()
        {
            Edges("RestoreCrateSystem", Relation.Creates, "CrateRestored").Should().ContainSingle(e =>
                e.Entity == "group" && e.Confidence == Confidence.Extracted && e.OnlyWhile == "component:JustSpawned");
            Edges("LabelCratesSystem", Relation.Creates, "CrateLabelled").Should().ContainSingle(e => e.OnlyWhile == null);
            Edges("UnmarkJustSpawnedSystem", Relation.Creates, "SpawnSeen").Should().ContainSingle(e => e.OnlyWhile == "component:JustSpawned",
                "the unmarking loop sets SpawnSeen while the crate still has JustSpawned");

            var json = Graph.ToJson();
            json.Should().Contain("\"only_while\": \"component:JustSpawned\"").And.Contain("\"transient_removed_by\": [");
            EcsGraph.FromJson(json).Edges.Single(e => e.Source == "system:Sample.RestoreCrateSystem" && e.Target == "component:CrateRestored")
                .OnlyWhile.Should().Be("component:JustSpawned");
        }

        [Fact]
        public void The_report_lists_a_flag_set_only_on_transient_entities_as_set_once_and_says_why()
        {
            var report = GraphReport.Build(Graph);
            var neverCleared = Section(report, "State flags that are never cleared");
            var setOnce = Section(report, "Flags set once on purpose?");

            setOnce.Should().Contain("- `CrateRestored` set by `RestoreCrateSystem` — the setter only sees entities while they have `JustSpawned`, " +
                                     "which `UnmarkJustSpawnedSystem` removes — Components.cs:41\n");
            neverCleared.Should().Contain("- `CrateLabelled` set by `LabelCratesSystem` — Components.cs:42\n", "its setter's group requires nothing transient")
                .And.NotContain("`CrateRestored`");
        }

        [Fact]
        public void A_guarded_setter_stays_never_cleared_beside_an_unguarded_one_or_when_it_reacts_to_a_change()
        {
            // A second setter without the guard: the flag can be set again on any crate.
            var graph = EcsGraph.FromJson(Graph.ToJson());
            graph.Edges.Add(new GraphEdge { Source = "system:Sample.LabelCratesSystem", Target = "component:CrateRestored", Relation = Relation.Creates, Via = "Flag", Entity = "group" });
            graph.Sort();
            Section(GraphReport.Build(graph), "State flags that are never cleared").Should().Contain("`CrateRestored`");

            // The guarded setter reacts to a [Watched] change: it sets the flag once while the change comes again.
            graph = EcsGraph.FromJson(Graph.ToJson());
            graph.Edges.Add(new GraphEdge { Source = "system:Sample.RestoreCrateSystem", Target = "component:Selected", Relation = Relation.Reacts, Via = "AllOf" });
            graph.Sort();
            Section(GraphReport.Build(graph), "State flags that are never cleared").Should().Contain("`CrateRestored`");
        }

        [Fact]
        public void A_cleanup_component_as_the_guard_is_named_as_such()
        {
            var graph = EcsGraph.FromJson(Graph.ToJson());
            graph.Edges.Single(e => e.Source == "system:Sample.RestoreCrateSystem" && e.Target == "component:CrateRestored").OnlyWhile = "component:Tapped";

            Section(GraphReport.Build(graph), "Flags set once on purpose?").Should().Contain(
                "- `CrateRestored` set by `RestoreCrateSystem` — the setter only sees entities while they have `Tapped`, which [Cleanup] removes — ");
        }

        static string Section(string report, string title)
        {
            var start = report.IndexOf("### " + title, StringComparison.Ordinal);
            start.Should().BeGreaterThan(0, $"the report has a '{title}' section");
            var end = report.IndexOf("\n### ", start + 4, StringComparison.Ordinal);
            return end < 0 ? report.Substring(start) : report.Substring(start, end - start);
        }

        // -- reactions to a [Watched] change -------------------------------------------------------

        [Fact]
        public void Explain_names_who_reacts_to_a_watched_write_and_what_they_hand_off()
        {
            var output = Queries.Explain("CommitCartSystem");

            output.Split('\n').Should().Contain(
                "    Unpaid  via Flag  Systems/ShopSystems.cs:64  → reacted to by SendPurchaseEventOnPaidSystem (creates ItemPurchasedEvent)");
            output.Should().NotContain("RemoveUnpaidChangedGameSystem", "the generated cleanup is no reaction worth following");
            Queries.Explain("SelectSystem").Should().Contain("  → reacted to by HighlightOnSelectedSystem");
            Queries.Explain("DamageSystem").Should().NotContain("reacted to by", "Health is not [Watched]");
        }

        [Fact]
        public void The_reactors_named_on_a_line_are_capped_keeping_those_that_hand_off()
        {
            var graph = EcsGraph.FromJson(Graph.ToJson());
            var unpaid = graph.Nodes.Single(n => n.Label == "Unpaid").Id;
            var purchased = graph.Nodes.Single(n => n.Label == "ItemPurchasedEvent").Id;
            for (var i = 1; i <= 4; i++)
            {
                var id = $"system:Sample.Reactor{i}";
                graph.Nodes.Add(new GraphNode { Id = id, Label = $"Reactor{i}", Kind = NodeKind.System });
                graph.Edges.Add(new GraphEdge { Source = id, Target = unpaid, Relation = Relation.Reacts, Via = "AllOf" });
            }
            graph.Edges.Add(new GraphEdge { Source = "system:Sample.Reactor4", Target = purchased, Relation = Relation.Creates, Via = "Add" });
            graph.Sort();

            // Execution order: SendPurchaseEventOnPaidSystem (placed), then Reactor1–4 (unplaced). Three
            // fit on a line; the two that create an event are kept, then the first of the rest.
            new GraphQueries(graph).Explain("CommitCartSystem").Should().Contain(
                "  → reacted to by SendPurchaseEventOnPaidSystem (creates ItemPurchasedEvent), Reactor1, Reactor4 (creates ItemPurchasedEvent), +2 more\n");
        }

        [Fact]
        public void An_XChanged_name_resolves_to_its_watched_component()
        {
            Queries.Resolve("UnpaidChanged").Label.Should().Be("Unpaid");
            Queries.Resolve("UnpaidChangedComponent").Label.Should().Be("Unpaid");
            Queries.Resolve("selectedchanged", NodeKind.Component).Label.Should().Be("Selected");

            Action notWatched = () => Queries.Resolve("FurnitureChanged");
            notWatched.Should().Throw<NodeNotFoundException>("Furniture has no [Watched] marker");
        }

        [Fact]
        public void Who_reads_XChanged_answers_with_the_reactions()
        {
            var output = Queries.WhoReads("UnpaidChanged");

            output.Should().StartWith("UnpaidChanged — reacted to by 2 (2 direct, 0 inferred)\n" +
                                      "  UnpaidChanged is Unpaid's [Watched] marker: every add, replace or remove of Unpaid sets it for one frame. Listed: the systems reacting to it.\n");
            output.Should().Contain("  SendPurchaseEventOnPaidSystem  [AllOf]  ShopFeature #5, GameplayFeature order 16\n      reacts via AllOf");
            output.Should().NotContain("TickOffersSystem", "it reads Unpaid, not its change").And.NotContain("filters via");
            Queries.WhoReads("UnpaidChangedComponent").Should().Be(output);
        }

        [Fact]
        public void Who_creates_and_lifecycle_of_XChanged_follow_the_changes_of_the_component()
        {
            var creates = Queries.WhoCreates("UnpaidChanged");
            creates.Should().StartWith("UnpaidChanged — set by 2 (2 direct, 0 inferred)\n");
            creates.Should().Contain("Listed: who adds or removes Unpaid.")
                .And.Contain("  BuyItemSystem  [Flag]").And.Contain("      creates via Flag")
                .And.Contain("  CommitCartSystem  [Flag]").And.Contain("      removes via Flag");

            var lifecycle = Queries.Lifecycle("UnpaidChanged");
            var lines = lifecycle.Split('\n').Select(l => l.Trim()).ToList();
            lifecycle.Should().StartWith("UnpaidChanged  [Game, [Watched] marker of Unpaid]\n  set by 2 · reacted to by 2\n");
            lines.Should().ContainInOrder(
                "#12   creates                BuyItemSystem  (ShopFeature)",
                "#15   removes                CommitCartSystem  (ShopFeature)",
                "#16   reacts                 SendPurchaseEventOnPaidSystem  (ShopFeature)");
            lifecycle.Should().NotContain("TickOffersSystem").And.NotContain("destroys", "the marker's life is the changes and the reactions");
        }

        [Fact]
        public void Explain_of_XChanged_explains_the_component_and_says_so()
        {
            var output = Queries.Explain("UnpaidChanged");

            output.Should().StartWith("Unpaid  [component]\n  UnpaidChanged is Unpaid's [Watched] marker:");
            output.Should().Contain("  reacted to by (2):");
        }

        [Fact]
        public void Who_creates_who_reads_and_lifecycle_of_a_watched_component_name_its_reactions_up_front()
        {
            const string line = "  reacted to by (through UnpaidChanged, on every add, replace or remove): SendPurchaseEventOnPaidSystem (creates ItemPurchasedEvent)\n";

            Queries.WhoCreates("Unpaid").Should().StartWith("Unpaid — created by 1 (1 direct, 0 inferred)\n" + line);
            Queries.WhoReads("Unpaid").Should().StartWith("Unpaid — read by 5 (5 direct, 0 inferred)\n" + line);
            Queries.Lifecycle("Unpaid").Should().Contain(" · destroyed by 1\n" + line);

            // In the head, so it survives the budget: the reactions are what a capped answer would cut.
            new GraphQueries(Graph, budget: 10).WhoReads("Unpaid").Should().Contain(line).And.Contain("not shown");
            Queries.WhoCreates("Furniture").Should().NotContain("reacted to by");
        }

        [Fact]
        public void Path_labels_a_hop_by_the_edge_that_carries_data()
        {
            var output = Queries.Path("CommitCartSystem", "CountPurchasesSystem");

            // SendPurchaseEventOnPaidSystem both reacts to UnpaidChanged and filters NoneOf(Unpaid).
            output.Should().StartWith("CommitCartSystem → CountPurchasesSystem: 4 hops\n");
            output.Should().Contain("  CommitCartSystem --removes--> Unpaid  [EXTRACTED] via Flag")
                .And.Contain("  Unpaid --reacted to by--> SendPurchaseEventOnPaidSystem  [EXTRACTED] via AllOf")
                .And.Contain("  SendPurchaseEventOnPaidSystem --creates--> ItemPurchasedEvent")
                .And.NotContain("filtered by");
        }

        [Fact]
        public void Query_walks_reactions_and_names_them_on_the_write()
        {
            var output = Queries.Query("commit cart");

            output.Should().Contain("    removes Unpaid  via Flag  ShopSystems.cs:64  → reacted to by SendPurchaseEventOnPaidSystem (creates ItemPurchasedEvent)\n");
            output.Should().Contain("    reacted to by SendPurchaseEventOnPaidSystem  via AllOf  ShopSystems.cs:75\n", "query walks reacts edges");
            output.Should().Contain("\n  SendPurchaseEventOnPaidSystem  [system; ShopFeature #5, GameplayFeature order 16]");
        }

        [Fact]
        public void A_write_others_react_to_survives_the_per_node_cap_of_a_query()
        {
            // Eight more plain reads put CommitCartSystem over six links: without counting the
            // change of Unpaid as a hand-off, the reads of one-neighbour components would win.
            var graph = EcsGraph.FromJson(Graph.ToJson());
            var commit = graph.Nodes.Single(n => n.Label == "CommitCartSystem").Id;
            for (var i = 1; i <= 8; i++)
            {
                graph.Nodes.Add(new GraphNode { Id = $"component:Extra{i}", Label = $"Extra{i}", Kind = NodeKind.Component, Contexts = { "Game" }, Flag = true });
                graph.Edges.Add(new GraphEdge { Source = commit, Target = $"component:Extra{i}", Relation = Relation.Reads, Via = "Has" });
            }
            graph.Sort();

            var capped = new GraphQueries(graph, budget: 60).Query("commit cart");

            capped.Should().Contain(", at most 6 links per node\n");
            capped.Should().Contain("    removes Unpaid  via Flag  ShopSystems.cs:64  → reacted to by SendPurchaseEventOnPaidSystem");
        }
    }
}
