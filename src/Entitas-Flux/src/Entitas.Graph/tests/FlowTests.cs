using System;
using System.Collections.Generic;
using System.Linq;
using Entitas.Graph.Output;
using FluentAssertions;
using Xunit;

namespace Entitas.Graph.Tests
{
    /// <summary>`flow`: the chain after a component or system, frames, hubs, references, the budget — on the Fixture/ project.</summary>
    public class FlowTests
    {
        static readonly GraphQueries Queries = new GraphQueries(FixtureProject.Graph);

        [Fact]
        public void A_request_leads_to_its_handler_the_event_it_raises_and_that_event_s_reader()
        {
            var output = Queries.Flow("PlaceFurnitureRequest");

            output.Should().StartWith("flow PlaceFurnitureRequest  [Game, data, request] — 2 systems, 2 hops; frames under GameplayFeature\n");
            Lines(output).Should().ContainInOrder(
                "created by RequestPlacementSystem #2 (FurnitureFeature) [+0]  FurnitureSystems.cs:17",
                "read by PlaceFurnitureByRequestSystem  #3 (FurnitureFeature)  same frame [+0]  FurnitureSystems.cs:31",
                "creates FurniturePlacedEvent  [event]  FurnitureSystems.cs:39",
                "read by UnlockAchievementSystem  #10  next frame (waits for Ready from MarkEventsReadySystem #1) [+1]  EventSystems.cs:15",
                "ends: destroyed by DestroyEventsSystem*  LateFeature #1  (created with Event + FurniturePlacedEvent; Ready by MarkEventsReadySystem)  EventSystems.cs:54",
                "ends: destroyed by DestroyDestructedSystem*  LateFeature #2  (created with PlaceFurnitureRequest; Destructed by PlaceFurnitureByRequestSystem)  EventSystems.cs:68");

            // Indented as a tree: the event under its producer, its reader under the event.
            Indent(output, "read by PlaceFurnitureByRequestSystem").Should().Be(2);
            Indent(output, "creates FurniturePlacedEvent").Should().Be(4);
            Indent(output, "read by UnlockAchievementSystem").Should().Be(6);
        }

        [Fact]
        public void A_system_s_flow_starts_at_its_own_writes_and_says_where_a_request_ends()
        {
            var output = Queries.Flow("RequestPlacementSystem");

            Lines(output).Should().ContainInOrder(
                "RequestPlacementSystem  #2 (FurnitureFeature) [+0]  Systems/FurnitureSystems.cs:6",
                "creates PlaceFurnitureRequest  [request]  FurnitureSystems.cs:17",
                "read by PlaceFurnitureByRequestSystem  #3 (FurnitureFeature)  same frame [+0]  FurnitureSystems.cs:31",
                "creates OrphanRequest  [request]  FurnitureSystems.cs:18",
                "ends: nothing removes or destroys it");
        }

        [Fact]
        public void Removing_a_watched_component_hands_off_to_the_systems_reacting_to_its_marker()
        {
            var output = Queries.Flow("CommitCartSystem");

            // The hop assistants missed: nothing connects CommitCartSystem to the reaction but the
            // [Watched] marker, and the event the reaction raises is read the next frame.
            Lines(output).Should().ContainInOrder(
                "CommitCartSystem  #15 (ShopFeature) [+0]  Systems/ShopSystems.cs:55",
                "removes Unpaid  [Watched → UnpaidChanged]  ShopSystems.cs:64",
                "reacted to by SendPurchaseEventOnPaidSystem  #16 (ShopFeature)  same frame [+0]  ShopSystems.cs:75",
                "creates ItemPurchasedEvent  [event]  ShopSystems.cs:83",
                "read by CountPurchasesSystem  #17 (ShopFeature)  next frame (waits for Ready from MarkEventsReadySystem #1) [+1]  ShopSystems.cs:94",
                "ends: UnpaidChanged cleared by RemoveUnpaidChangedGameSystem  not in any feature, generated");
        }

        [Fact]
        public void An_add_does_not_reach_reactors_that_leave_the_component_out()
        {
            // SendPurchaseEventOnPaidSystem reacts to UnpaidChanged with NoneOf(Unpaid): the purchase,
            // not the item going into the cart.
            var output = Queries.Flow("BuyItemSystem");

            output.Should().NotContain("reacted to by");
            Lines(output).Should().Contain("also, not followed: creates ShopItem, Unpaid (its 1 reactor takes the other change)");

            // Asked about Unpaid itself, the reaction counts from the system that removes it.
            Lines(Queries.Flow("Unpaid")).Should().ContainInOrder(
                "changed by 2 [+0]: BuyItemSystem #12 (ShopFeature), CommitCartSystem #15 (ShopFeature)",
                "reacted to by SendPurchaseEventOnPaidSystem  #16 (ShopFeature)  same frame [+0]  ShopSystems.cs:75");
        }

        [Fact]
        public void A_taker_ordered_before_its_producer_sees_it_the_next_frame()
        {
            // DeselectSystem (#7) removes Selected; HighlightOnSelectedSystem (#6) reacts to it, and
            // in the fixture nothing clears SelectedChanged in between.
            Lines(Queries.Flow("DeselectSystem")).Should().Contain(
                "reacted to by HighlightOnSelectedSystem  #6 (FurnitureFeature)  next frame [+1]  FurnitureSystems.cs:84");
        }

        [Fact]
        public void Hubs_are_listed_not_walked()
        {
            var output = new GraphQueries(FixtureProject.Graph, hubThreshold: 1).Flow("PlaceFurnitureRequest");

            Lines(output).Should().Contain(l => l.StartsWith("also, not followed: creates ", StringComparison.Ordinal) &&
                                                l.Contains("Event* (hub)") && l.Contains("FurniturePlacedEvent (hub)"));
            output.Should().NotContain("UnlockAchievementSystem", "FurniturePlacedEvent is a hub at this threshold, so its reader is not reached");
        }

        [Fact]
        public void A_node_shown_before_is_referred_to_not_expanded_again()
        {
            var output = Queries.Flow("SelectSystem");

            Lines(output).Should().ContainInOrder(
                "creates Selected  [transient, Watched → SelectedChanged]  FurnitureSystems.cs:71",
                "read+removed by DeselectSystem  #7 (FurnitureFeature)  same frame [+0]  FurnitureSystems.cs:100",
                "removes Selected  [Watched → SelectedChanged]  FurnitureSystems.cs:105  (see above)");
            Lines(output).Count(l => l.Contains(" Selected  [")).Should().Be(2);

            // Reached twice, expanded where it is first reached in the walk, referred to elsewhere.
            var furniture = Lines(Queries.Flow("Furniture"));
            furniture.Count(l => l.Contains(" HighlightOnSelectedSystem  ")).Should().Be(2);
            furniture.Count(l => l.Contains(" HighlightOnSelectedSystem  ") && l.EndsWith("(see below)", StringComparison.Ordinal)).Should().Be(1);
        }

        [Fact]
        public void Depth_counts_system_hops()
        {
            var output = Queries.Flow("RequestPlacementSystem", depth: 1);

            Lines(output).Should().Contain("… taken by 1 more system, past --depth 1");
            output.Should().NotContain("UnlockAchievementSystem");
            output.Should().StartWith("flow RequestPlacementSystem  [system; FurnitureFeature #1, GameplayFeature order 2] — 2 systems, 1 hop;");
        }

        [Fact]
        public void An_answer_over_the_budget_names_every_branch_in_its_summary()
        {
            var output = new GraphQueries(FixtureProject.Graph, budget: 60).Flow("RequestPlacementSystem");

            Lines(output).Should().ContainInOrder(
                "flow: 3 systems, 3 hand-offs, 2 hops",
                "PlaceFurnitureRequest → PlaceFurnitureByRequestSystem, UnlockAchievementSystem; then FurniturePlacedEvent",
                "OrphanRequest → no system takes it");
            output.Should().Contain("more lines not shown — the answer is capped at ~60 tokens (--budget 0 prints everything).");
            Queries.Flow("RequestPlacementSystem").Should().NotContain("not shown", "the default budget fits every fixture flow");
        }

        [Fact]
        public void The_marker_s_name_stands_for_the_component_and_a_feature_is_no_start()
        {
            Lines(Queries.Flow("SelectedChanged")).Should().Contain(
                "SelectedChanged is Selected's [Watched] marker: shown are the systems reacting to Selected's changes.");
            Queries.Flow("GameplayFeature").Should().StartWith("GameplayFeature is a feature: flow follows data from a component or a system");
        }

        [Fact]
        public void A_write_made_only_under_a_condition_says_which()
        {
            var output = Queries.Flow("OpenShopSystem");

            // SettleTillSystem creates one event if the till went negative, the other else: both are
            // followed, each with its own condition, so a caller can tell which branch is its own.
            Lines(output).Should().ContainInOrder(
                "OpenShopSystem  #21 (ShopFeature) [+0]  Systems/ShopSystems.cs:153",
                "creates Till  [request, transient]  ShopSystems.cs:166",
                "read+removed by SettleTillSystem  #22 (ShopFeature)  same frame [+0]  ShopSystems.cs:177",
                "creates CoinsSpentEvent  [event]  (if till.Till < 0)  ShopSystems.cs:184",
                "read by CountCoinsSystem  #24 (ShopFeature)  next frame (waits for Ready from MarkEventsReadySystem #1) [+1]  ShopSystems.cs:210",
                "creates CoinsEarnedEvent  [event]  (if !(till.Till < 0))  ShopSystems.cs:186");

            // Cut to its summary, a branch still says it is conditional.
            Lines(new GraphQueries(FixtureProject.Graph, budget: 60).Flow("SettleTillSystem")).Should().ContainInOrder(
                "CoinsSpentEvent (if till.Till < 0) → CountCoinsSystem",
                "CoinsEarnedEvent (if !(till.Till < 0)) → CountCoinsSystem");

            // The other commands say it on the write's line, briefly.
            Lines(Queries.Explain("SettleTillSystem")).Should().Contain("CoinsSpentEvent  via Add (if till.Till < 0)  Systems/ShopSystems.cs:184");
            Lines(Queries.WhoCreates("CoinsEarnedEvent")).Should().Contain("creates via Add (if !(till.Till < 0))  Systems/ShopSystems.cs:186");
        }

        [Fact]
        public void What_a_system_writes_by_calling_a_factory_is_one_line_after_its_own_writes()
        {
            var output = Queries.Flow("OpenShopSystem");
            var lines = Lines(output);

            // One line for everything the factory sets, worded so the system is the one that
            // creates (its line, under it at its own indent), with the call's condition; the event
            // made through the call is still followed, under that line.
            lines.Should().ContainInOrder(
                "creates Till  [request, transient]  ShopSystems.cs:166",
                "by calling ShopkeeperFactory.Create: creates 3 (ShopOpenedEvent, Event, Shopkeeper)  (if _keepers.count == 0)  Infrastructure.cs:65",
                "creates ShopOpenedEvent  [event]  Infrastructure.cs:66",
                "read by GreetCustomersSystem  #23 (ShopFeature)  next frame (waits for Ready) [+1]  ShopSystems.cs:197");
            Indent(output, "creates Till").Should().Be(2);
            Indent(output, "by calling ShopkeeperFactory.Create").Should().Be(2);
            Indent(output, "creates ShopOpenedEvent").Should().Be(4);
            lines.Should().NotContain(l => l.StartsWith("creates Shopkeeper", StringComparison.Ordinal), "state set through a call is named on its line only");
            output.Should().NotContain("via Add*", "the call line says how, once");

            // A call writing one component and handing nothing on stays among the writes not followed.
            Lines(Queries.Flow("SettleTillSystem")).Should().Contain("also, not followed: creates Event*; removes Till");

            // Whoever reads the graph elsewhere sees the same call, and its condition.
            Lines(Queries.WhoCreates("ShopOpenedEvent")).Should().Contain("creates via Add (ShopkeeperFactory.Create) (if _keepers.count == 0)  Infrastructure.cs:66");
        }

        [Fact]
        public void Through_a_call_an_add_is_named_and_a_change_a_system_sees_is_followed()
        {
            // MakerSystem calls Factory.Make, which adds the [Watched] Built (an initial value) and,
            // through Wear.Off, removes the [Watched] Worn (a change). Both have a reactor after it.
            var graph = new EcsGraph();
            graph.Nodes.Add(new GraphNode { Id = "Root", Label = "Root", Kind = NodeKind.Feature, Root = true });
            var built = Component(graph, "Built", "Watched");
            var worn = Component(graph, "Worn", "Watched");
            var maker = System(graph, "MakerSystem", "Root", 1);
            graph.Edges.Add(new GraphEdge
            {
                Source = maker, Target = built, Relation = Relation.Creates, Via = "Add", Confidence = Confidence.Inferred,
                Through = "Factory.Make", SourceFile = "Factory.cs", Line = 3
            });
            graph.Edges.Add(new GraphEdge
            {
                Source = maker, Target = worn, Relation = Relation.Removes, Via = "Flag", Confidence = Confidence.Inferred,
                Through = "Wear.Off", Called = "Factory.Make", SourceFile = "Wear.cs", Line = 7
            });
            Edge(graph, System(graph, "OnBuiltSystem", "Root", 2), Relation.Reacts, built);
            Edge(graph, System(graph, "OnWornSystem", "Root", 3), Relation.Reacts, worn);

            var lines = Lines(new GraphQueries(graph, budget: 0).Flow("MakerSystem"));

            lines.Should().ContainInOrder(
                "by calling Factory.Make: creates Built; removes Worn  Factory.cs:3",
                "removes Worn  [Watched → WornChanged]  Wear.cs:7",
                "reacted to by OnWornSystem  #3  same frame [+0]  Code.cs:4");
            lines.Should().NotContain(l => l.Contains("OnBuiltSystem"), "an add through a call is the initial value of what the factory builds");
        }

        [Fact]
        public void A_watched_change_through_a_call_that_its_reactor_never_sees_is_listed_with_the_note()
        {
            // HireSystem (#10) calls Factory.Convert, which sets Role and, through Cats.Release,
            // clears the [Watched] Busy. Its one reactor runs at #5, and the generated cleanup — a
            // cleanup system, so after every execute one — clears BusyChanged before #5 comes round.
            var graph = new EcsGraph();
            graph.Nodes.Add(new GraphNode { Id = "Root", Label = "Root", Kind = NodeKind.Feature, Root = true });
            var busy = Component(graph, "Busy", "Watched");
            var role = Component(graph, "Role");
            var hire = System(graph, "HireSystem", "Root", 10);
            graph.Edges.Add(new GraphEdge
            {
                Source = hire, Target = role, Relation = Relation.Creates, Via = "Replace", Confidence = Confidence.Inferred,
                Through = "Factory.Convert", SourceFile = "Factory.cs", Line = 3, Condition = "slots > 0 && cat.isCustomer"
            });
            graph.Edges.Add(new GraphEdge
            {
                Source = hire, Target = busy, Relation = Relation.Removes, Via = "Flag", Confidence = Confidence.Inferred,
                Through = "Cats.Release", Called = "Factory.Convert", SourceFile = "Cats.cs", Line = 7, Condition = "slots > 0 && cat.isCustomer"
            });
            Edge(graph, System(graph, "AnimateSystem", "Root", 5), Relation.Reacts, busy);
            Edge(graph, System(graph, "RemoveBusyChangedSystem", "Root", 3, "Cleanup", generated: true), Relation.Reacts, busy);

            var output = new GraphQueries(graph, budget: 0).Flow("HireSystem");

            Lines(output).Should().ContainInOrder(
                "by calling Factory.Convert: creates Role; removes Busy  (if slots > 0 && cat.isCustomer)  Factory.cs:3",
                "removes Busy  [Watched → BusyChanged]  Cats.cs:7",
                "reacted to by AnimateSystem  #5  never sees it (RemoveBusyChangedSystem #3 clears BusyChanged first)  Code.cs:3");
            Indent(output, "removes Busy").Should().Be(Indent(output, "by calling Factory.Convert") + 2, "listed under the call's line, which carries the condition");
        }

        [Fact]
        public void A_line_whose_writes_are_made_in_an_if_and_its_else_keeps_only_what_they_share()
        {
            // AddX in the if, ReplaceX in the else: two edges, one line, the same enclosing case.
            var graph = new EcsGraph();
            graph.Nodes.Add(new GraphNode { Id = "Root", Label = "Root", Kind = NodeKind.Feature, Root = true });
            var request = Component(graph, "PayRequest", role: "request");
            var payer = System(graph, "PayerSystem", "Root", 1);
            foreach (var (via, condition) in new[] { ("Add", "cart.isReady && !wallet.hasPending"), ("Replace", "cart.isReady && wallet.hasPending") })
                graph.Edges.Add(new GraphEdge
                {
                    Source = payer, Target = request, Relation = Relation.Creates, Via = via, SourceFile = "Pay.cs", Line = via == "Add" ? 4 : 6, Condition = condition
                });
            Edge(graph, System(graph, "PaySystem", "Root", 2), Relation.Reads, request);

            Lines(new GraphQueries(graph, budget: 0).Flow("PayerSystem")).Should().Contain("creates PayRequest  [request]  (if cart.isReady)  Pay.cs:4");
        }

        [Fact]
        public void A_reader_waits_for_what_the_new_entity_is_made_without()
        {
            // ReturnParcelsSystem (#10) makes a parcel with its model's asset and both requests on it.
            // DispatchParcelsSystem needs the Model, which ModelBehaviour attaches once ModelLoader's
            // await is over: no frame can be told. FrankParcelsSystem needs the Stamp, which
            // StampParcelsSystem (#9) adds — the next frame, since it runs before the parcel is made.
            var output = Queries.Flow("ReturnParcelsSystem");

            Lines(output).Should().ContainInOrder(
                "ReturnParcelsSystem  #10 [+0]  Systems/ParcelSystems.cs:20",
                "creates DispatchPending  [request]  ParcelSystems.cs:27",
                "read+removed by DispatchParcelsSystem  #13  frame unknown (waits for Model, added after an await)  ParcelSystems.cs:68",
                "creates FrankPending  [request]  ParcelSystems.cs:28",
                "read+removed by FrankParcelsSystem  #14  next frame (waits for Stamp from StampParcelsSystem #9) [+1]  ParcelSystems.cs:86");

            // A parcel made with its Model is dispatched by order alone, the same frame.
            Lines(Queries.Flow("ResendParcelsSystem")).Should().Contain(
                "read+removed by DispatchParcelsSystem  #13  same frame [+0]  ParcelSystems.cs:68");

            // Without the waits, as graph.json before them said it: the same frame for both.
            var unaware = EcsGraph.FromJson(FixtureProject.Graph.ToJson());
            foreach (var edge in unaware.Edges)
                edge.Waits.Clear();
            Lines(new GraphQueries(unaware).Flow("ReturnParcelsSystem")).Should().Contain(
                "read+removed by FrankParcelsSystem  #14  same frame [+0]  ParcelSystems.cs:86");
        }

        [Fact]
        public void A_wait_names_its_setters_and_leaves_the_frame_unknown_where_they_disagree_or_run_elsewhere()
        {
            // MakerSystem (#10) makes an entity without Lit and puts LookRequest on it; five readers
            // each need what a different set of systems adds.
            var graph = new EcsGraph();
            graph.Nodes.Add(new GraphNode { Id = "MainRoot", Label = "MainRoot", Kind = NodeKind.Feature, Root = true });
            graph.Nodes.Add(new GraphNode { Id = "OtherRoot", Label = "OtherRoot", Kind = NodeKind.Feature, Root = true });
            var request = Component(graph, "LookRequest", role: "request");
            var maker = System(graph, "MakerSystem", "MainRoot", 10);
            var early = System(graph, "EarlyLightSystem", "MainRoot", 2);
            var earlier = System(graph, "EarlierLightSystem", "MainRoot", 1);
            var later = System(graph, "LateLightSystem", "MainRoot", 20);
            var elsewhere = System(graph, "ElsewhereLightSystem", "OtherRoot", 1);
            foreach (var lit in new[] { "Lit", "Glow", "Shine", "Spark", "Flare" })
                Component(graph, lit);

            Edge(graph, maker, Relation.Creates, request);
            var waits = graph.Edges[0].Waits;
            void Reader(string system, int order, string needs, string? added, params string[] by)
            {
                Edge(graph, System(graph, system, "MainRoot", order), Relation.Reads, request);
                var wait = new ReaderWait { System = system, Needs = needs, Added = added };
                wait.AddedBy.AddRange(by);
                waits.Add(wait);
            }

            Reader("OneSetterSystem", 30, "Lit", null, early);
            Reader("TwoSettersSystem", 31, "Glow", null, early, earlier);
            Reader("DisagreeSystem", 32, "Shine", null, early, later);
            Reader("ElsewhereSystem", 33, "Spark", null, elsewhere);
            Reader("OutsideSystem", 34, "Flare", "outside the systems");

            var lines = Lines(new GraphQueries(graph, budget: 0).Flow("MakerSystem"));

            lines.Should().Contain(l => l.StartsWith("read by OneSetterSystem  #30  next frame (waits for Lit from EarlyLightSystem #2) [+1]", StringComparison.Ordinal));
            lines.Should().Contain(l => l.StartsWith("read by TwoSettersSystem  #31  next frame (waits for Glow from EarlyLightSystem #2 or 1 more) [+1]", StringComparison.Ordinal));
            lines.Should().Contain(l => l.StartsWith("read by DisagreeSystem  #32  frame unknown (waits for Shine; the frame depends on which of 2 systems adds it)", StringComparison.Ordinal));
            lines.Should().Contain(l => l.StartsWith("read by ElsewhereSystem  #33  frame unknown (waits for Spark; ElsewhereLightSystem runs under OtherRoot, not MainRoot)", StringComparison.Ordinal));
            lines.Should().Contain(l => l.StartsWith("read by OutsideSystem  #34  frame unknown (waits for Flare, added outside the systems)", StringComparison.Ordinal));

            // One write of the producer that leaves a reader out is enough for it to see the request whenever it runs.
            graph.Edges.Add(new GraphEdge { Source = maker, Target = request, Relation = Relation.Creates, Via = "Replace", SourceFile = "Code.cs", Line = 99 });
            Lines(new GraphQueries(graph, budget: 0).Flow("MakerSystem")).Should().Contain(l =>
                l.StartsWith("read by OutsideSystem  #34  same frame [+0]", StringComparison.Ordinal));
        }

        [Fact]
        public void The_install_block_names_flow_and_stays_short()
        {
            var block = Setup.InstructionBlock.Build("/repo", "/repo/game", "/repo/game/Assets/Entitas/Entitas/Tools~/entitas-graph.dll", "/repo");

            block.Should().Contain("flow <Request|System>");
            block.Split('\n').Length.Should().BeLessOrEqualTo(15);
        }

        // -- frames the fixture cannot show without moving every order in it -----------------------

        [Fact]
        public void A_reactor_running_before_the_change_never_sees_it_when_the_cleanup_clears_the_marker_first()
        {
            var output = new GraphQueries(Frames(), budget: 0).Flow("ProducerSystem");

            // The generated cleanup is ordered #3 but runs in the Cleanup phase, after every Execute.
            Lines(output).Should().Contain("reacted to by LateReactorSystem  #20  same frame [+0]  Code.cs:4");
            Lines(output).Should().Contain(l => l.StartsWith("reacted to by EarlyReactorSystem  #5  never sees it (RemoveWorkChangedSystem #3 clears WorkChanged first)", StringComparison.Ordinal));
        }

        [Fact]
        public void Another_root_or_a_remover_in_between_leaves_the_frame_unknown()
        {
            var lines = Lines(new GraphQueries(Frames(), budget: 0).Flow("ProducerSystem"));

            lines.Should().Contain(l => l.StartsWith("read+destroyed by HandlerSystem  #30  same frame [+0]", StringComparison.Ordinal));
            lines.Should().Contain(l => l.StartsWith("read by AfterHandlerSystem  #40  frame unknown (HandlerSystem #30 may destroy it first)", StringComparison.Ordinal));
            lines.Should().Contain(l => l.StartsWith("read by OtherRootSystem  OtherRoot #1  frame unknown (runs under OtherRoot, not MainRoot)", StringComparison.Ordinal));
        }

        [Fact]
        public void A_cleanup_system_runs_after_every_execute_system_whatever_its_number()
        {
            var lines = Lines(new GraphQueries(Frames(), budget: 0).Flow("SweepSystem"));

            lines.Should().Contain(l => l.StartsWith("read by FirstSystem  #1  next frame [+1]", StringComparison.Ordinal));
            lines.Should().Contain(l => l.StartsWith("read by LateSweepSystem  #4, Cleanup  same frame [+0]", StringComparison.Ordinal));
        }

        /// <summary>
        /// One root, MainRoot, and a second, OtherRoot. ProducerSystem (#10) removes the [Watched]
        /// Work and creates WorkRequest: a reactor runs before it (#5), one after (#20), and the
        /// generated cleanup is #3 but a cleanup system; HandlerSystem (#30) destroys the request
        /// before AfterHandlerSystem (#40) reads it; OtherRootSystem reads it under the other root.
        /// SweepSystem, a cleanup system at #2, creates SweepRequest, read by an execute system at
        /// #1 and a cleanup system at #4.
        /// </summary>
        static EcsGraph Frames()
        {
            var graph = new EcsGraph();
            graph.Nodes.Add(new GraphNode { Id = "MainRoot", Label = "MainRoot", Kind = NodeKind.Feature, Root = true });
            graph.Nodes.Add(new GraphNode { Id = "OtherRoot", Label = "OtherRoot", Kind = NodeKind.Feature, Root = true });
            var work = Component(graph, "Work", "Watched");
            var request = Component(graph, "WorkRequest", role: "request");
            var sweep = Component(graph, "SweepRequest", role: "request");

            var producer = System(graph, "ProducerSystem", "MainRoot", 10);
            Edge(graph, producer, Relation.Removes, work);
            Edge(graph, producer, Relation.Creates, request);
            Edge(graph, System(graph, "EarlyReactorSystem", "MainRoot", 5), Relation.Reacts, work);
            Edge(graph, System(graph, "LateReactorSystem", "MainRoot", 20), Relation.Reacts, work);
            var cleanup = System(graph, "RemoveWorkChangedSystem", "MainRoot", 3, "Cleanup", generated: true);
            Edge(graph, cleanup, Relation.Reacts, work);

            var handler = System(graph, "HandlerSystem", "MainRoot", 30);
            Edge(graph, handler, Relation.Reads, request);
            Edge(graph, handler, Relation.Destroys, request);
            Edge(graph, System(graph, "AfterHandlerSystem", "MainRoot", 40), Relation.Reads, request);
            Edge(graph, System(graph, "OtherRootSystem", "OtherRoot", 1), Relation.Reads, request);

            Edge(graph, System(graph, "SweepSystem", "MainRoot", 2, "Cleanup"), Relation.Creates, sweep);
            Edge(graph, System(graph, "FirstSystem", "MainRoot", 1), Relation.Reads, sweep);
            Edge(graph, System(graph, "LateSweepSystem", "MainRoot", 4, "Cleanup"), Relation.Reads, sweep);
            return graph;
        }

        static string Component(EcsGraph graph, string label, string? attribute = null, string? role = null)
        {
            var node = new GraphNode { Id = label, Label = label, Kind = NodeKind.Component, Role = role };
            node.Contexts.Add("Game");
            if (attribute != null) node.Attributes.Add(attribute);
            graph.Nodes.Add(node);
            return label;
        }

        static string System(EcsGraph graph, string label, string root, int order, string runs = "Execute", bool generated = false)
        {
            var node = new GraphNode { Id = label, Label = label, Kind = NodeKind.System, SourceFile = "Assets/" + label + ".cs", Line = 1, Generated = generated };
            node.Interfaces.Add(runs);
            node.Placements.Add(new Placement { Root = root, Order = order, Chain = { root } });
            node.RegisteredIn.Add(new Registration { Feature = root, Index = order });
            graph.Nodes.Add(node);
            return label;
        }

        static void Edge(EcsGraph graph, string source, string relation, string target) => graph.Edges.Add(new GraphEdge
        {
            Source = source, Target = target, Relation = relation,
            Via = relation switch { Relation.Reads or Relation.Reacts => "AllOf", Relation.Destroys => "Destroy", Relation.Removes => "Flag", _ => "Add" },
            SourceFile = "Code.cs", Line = graph.Edges.Count + 1
        });

        // -- reading an answer -----------------------------------------------------------------

        static List<string> Lines(string output) => output.Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0).ToList();

        static int Indent(string output, string start) =>
            output.Split('\n').First(l => l.TrimStart().StartsWith(start, StringComparison.Ordinal)) is var line ? line.Length - line.TrimStart().Length : -1;
    }
}
