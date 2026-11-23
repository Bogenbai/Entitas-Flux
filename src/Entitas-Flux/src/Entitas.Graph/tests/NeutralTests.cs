using System;
using System.Collections.Generic;
using System.Linq;
using Entitas.Graph.Analysis;
using Entitas.Graph.Output;
using Entitas.SourceGenerator;
using FluentAssertions;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Xunit;

namespace Entitas.Graph.Tests
{
    /// <summary>
    /// The FixtureNeutral/ space port, with the default options: its orders and pulses end in
    /// neither `Request` nor `Event`, so whatever the tool finds here it found by structure. It
    /// has the shapes Fixture/ has under other names — Pulse for Event, Armed for Ready, Doomed
    /// for Destructed, Fresh for JustCreated, features that do not end in "Feature" — and
    /// helpers whose parameters are named `gate` and `skip`.
    /// </summary>
    public class NeutralTests
    {
        static EcsGraph Graph => FixtureFolder.Neutral.Graph;
        static readonly GraphQueries Queries = new GraphQueries(FixtureFolder.Neutral.Graph, budget: 0);

        static GraphNode Node(string label) => Graph.Nodes.Single(n => n.Label == label);
        static string Label(string id) => Graph.Node(id)?.Label ?? id;

        static List<GraphEdge> Edges(string source, string relation, string target) =>
            Graph.Edges.Where(e => Label(e.Source) == source && e.Relation == relation && Label(e.Target) == target).ToList();

        /// <summary>"System needs Component" for the reactors a creates edge records as never seeing it.</summary>
        static IEnumerable<string> UnseenBy(string source, string target, string? through) =>
            Edges(source, Relation.Creates, target).Single(e => e.Through == through).UnseenBy.Select(u => $"{Label(u.System)} needs {Label(u.Needs)}");

        /// <summary>"System needs Component" for the reactors a write records as ones it may hide the change from.</summary>
        static IEnumerable<string> MayMiss(string source, string relation, string target) =>
            Edges(source, relation, target).Single().MayMiss.Select(m => $"{Label(m.System)} needs {Label(m.Needs)}");

        [Fact]
        public void The_port_compiles_with_its_generated_code()
        {
            CSharpGeneratorDriver.Create(new EntitasIncrementalGenerator())
                .RunGeneratorsAndUpdateCompilation(FixtureFolder.Neutral.Compile(), out var output, out _);
            output.GetDiagnostics().Where(d => d.Severity == DiagnosticSeverity.Error).Select(d => d.ToString()).Should().BeEmpty();
            Graph.Metadata.RequestSuffix.Should().Be("Request", "the default options");
            Graph.Nodes.Should().NotContain(n => n.Role != null, "no name ends in a suffix");
        }

        [Fact]
        public void Flow_from_an_order_follows_its_handler_the_pulse_it_makes_and_who_reads_that()
        {
            var output = Queries.Flow("DockingOrder");

            output.Should().StartWith("flow DockingOrder  [Port, data, request] — 6 systems, 3 hops; frames under CoreLoop");
            Lines(output).Should().ContainInOrder(
                "created by 2 [+0]: IssueDockingOrdersSystem #3 (Berthing), DockingPanel [type]; frames count from the 1 running under CoreLoop",
                "read+destroyed by DockShipsSystem  #5 (Berthing)  same frame [+0]  OrderSystems.cs:41",
                "creates ShipDocked  [event]  (if ship != null)  OrderSystems.cs:59",
                "read by UnloadCargoSystem  #9  next frame (waits for Armed from ArmPulsesSystem #1) [+1]  PulseSystems.cs:75",
                "creates CargoUnloaded  [event]  (if ship != null && ship.hasCargo)  PulseSystems.cs:85",
                "read by CountCargoSystem  #10  next frame (waits for Armed) [+2]  PulseSystems.cs:97",
                "ends: destroyed by SweepPulsesSystem*  LateLoop #1  (created with CargoUnloaded + Pulse; Armed by ArmPulsesSystem)  PulseSystems.cs:37",
                "read by GreetCaptainSystem  #8  next frame (waits for Armed) [+1]  PulseSystems.cs:61",
                "read by CountShipsDockedSystem  #11  next frame (waits for Armed) [+1]  PulseSystems.cs:112",
                "also, not followed: creates Docked, Pulse*",
                "read by LogOrdersSystem  #4 (Berthing)  same frame [+0]  OrderSystems.cs:26");

            // The marker and the plumbing are not followed: Pulse is no message, it carries them.
            Lines(output).Should().NotContain(l => l.StartsWith("creates Pulse", StringComparison.Ordinal));
        }

        [Fact]
        public void A_handler_marking_its_order_for_the_doom_sweeper_consumes_it()
        {
            var output = Queries.Flow("RefuelOrder");

            Lines(output).Should().ContainInOrder(
                "read by RefuelShipsSystem  #6 (Berthing)  next frame [+1]  OrderSystems.cs:75",
                "creates Fuel  [Watched → FuelChanged]  (if ship != null)  OrderSystems.cs:84",
                "also, not followed: creates Doomed",
                "ends: destroyed by SweepDoomedSystem*  LateLoop #2  (created with RefuelOrder; Doomed by RefuelShipsSystem)  PulseSystems.cs:52");
        }

        [Fact]
        public void A_depot_s_fuel_change_does_not_reach_the_reaction_that_needs_a_ship()
        {
            // The factory builds the depot and drains it through a helper: a [Watched] change on an
            // entity made right there, with Depot and Fuel and never Ship, which only the ship
            // factory puts on what it builds. WarnLowFuelSystem's group needs Ship.
            var output = Queries.Flow("BuildDepotsSystem");

            Lines(output).Should().ContainInOrder(
                "by calling DepotFactory.Build: creates 2 (Fuel, Depot)  (if _signal.hasClock && _signal.clock.Value % 20 == 0)  Plumbing.cs:55",
                "creates Fuel  [Watched → FuelChanged]  Plumbing.cs:56",
                "+1 reacting to FuelChanged need Ship, which this entity never gets");
            output.Should().NotContain("WarnLowFuelSystem");

            // Recorded on the writes, through the factory, the helper on the depot being built
            // and a loop over depots alike.
            UnseenBy("BuildDepotsSystem", "Fuel", "Tanks.Drain").Should().Equal("WarnLowFuelSystem needs Ship");
            UnseenBy("BuildDepotsSystem", "Fuel", "DepotFactory.Build").Should().Equal("WarnLowFuelSystem needs Ship");
            UnseenBy("LeakFuelSystem", "Fuel", null).Should().Equal("WarnLowFuelSystem needs Ship");
            Queries.Flow("LeakFuelSystem").Should().NotContain("WarnLowFuelSystem");

            // A ship found by its id is one the ship factory made: never a depot.
            UnseenBy("RefuelShipsSystem", "Fuel", null).Should().Equal("ShowGaugesSystem needs Depot");
            Lines(Queries.Flow("RefuelOrder")).Should().ContainInOrder(
                "creates Fuel  [Watched → FuelChanged]  (if ship != null)  OrderSystems.cs:84",
                "reacted to by WarnLowFuelSystem  #15 (Fleet)  same frame [+1]  ShipSystems.cs:106",
                "+1 reacting to FuelChanged need Depot, which this entity never gets");
        }

        [Fact]
        public void A_reaction_needing_what_the_entity_gets_later_stays_and_names_its_other_trigger()
        {
            // ShowGaugesSystem needs Gauge too, which no depot is built with: FitGaugesSystem fits
            // it to existing depots, so the depot can still match. It runs before the depots are
            // built, so it never sees this change — but it reacts to GaugeChanged as well.
            Lines(Queries.Flow("BuildDepotsSystem")).Should().Contain(
                "reacted to by ShowGaugesSystem  #20 (Depots)  never sees it (RemoveFuelChangedPortSystem #25 clears FuelChanged first; other trigger: GaugeChanged)  DepotSystems.cs:18");
            Edges("FitGaugesSystem", Relation.Creates, "Gauge").Should().OnlyContain(e => e.UnseenBy.Count == 0);

            // And a ship's fuel still reaches the ship's reaction.
            Lines(Queries.Flow("BurnFuelSystem")).Should().ContainInOrder(
                "reacted to by WarnLowFuelSystem  #15 (Fleet)  same frame [+0]  ShipSystems.cs:106",
                "+1 reacting to FuelChanged need Depot, which this entity never gets");
        }

        [Fact]
        public void A_reaction_needing_what_the_same_call_may_take_off_stays_marked()
        {
            // Unmooring lets the gantry go, whose magnetic grapple — reached through IGrapple and the
            // abstract Grapple — takes the Lodestone off the craft its gantry holds, then casts the
            // Hawser off. AlignLodestonesSystem needs the Lodestone, but that the grapple's craft
            // is the one unmoored holds only by how Gantry is used: it stays, marked.
            var output = Queries.Flow("UnmoorCraftsSystem");
            Lines(output).Should().ContainInOrder(
                "by calling Moorings.Unmoor: removes 3 (Hawser, Floodlight, Lodestone)  (if craft.hawser.Value <= 0)  Moorings.cs:72",
                "removes Hawser  [Watched → HawserChanged]  Moorings.cs:73",
                "reacted to by AlignLodestonesSystem (the same call may take off Lodestone, which it needs)  #30 (Probes)  same frame [+0]  MooringSystems.cs:48",
                // Nothing takes the Spool off; the Floodlight comes off the slipway, another parameter.
                "reacted to by ReelSpoolsSystem  #31 (Probes)  same frame [+0]  MooringSystems.cs:64",
                "reacted to by DimFloodlightsSystem  #32 (Probes)  same frame [+0]  MooringSystems.cs:80");
            MayMiss("UnmoorCraftsSystem", Relation.Removes, "Hawser").Should().Equal("AlignLodestonesSystem needs Lodestone");
            MayMiss("Moorings", Relation.Removes, "Hawser").Should().Equal("AlignLodestonesSystem needs Lodestone");
            Edges("UnmoorCraftsSystem", Relation.Removes, "Hawser").Should().OnlyContain(e => e.UnseenBy.Count == 0, "it may still see it");

            // Folded, the mark stays on the name.
            var folded = new GraphQueries(Graph, budget: output.Length / 4).Flow("UnmoorCraftsSystem");
            Lines(folded).Should().Contain(l => l.StartsWith(
                "reacted to by, ending there: AlignLodestonesSystem (the same call may take off Lodestone, which it needs) #30 (Probes), ReelSpoolsSystem #31 (Probes)",
                StringComparison.Ordinal));

            // Slackening snaps the Spool off only in a branch that returns before the Hawser changes.
            Lines(Queries.Flow("SlackenHawsersSystem")).Should().Contain("reacted to by ReelSpoolsSystem  #31 (Probes)  same frame [+0]  MooringSystems.cs:64");
            MayMiss("SlackenHawsersSystem", Relation.Creates, "Hawser").Should().BeEmpty();

            // A component start takes every change: the panel mooring a craft takes nothing off.
            Queries.Flow("Hawser").Should().NotContain("may take off");
        }

        [Fact]
        public void A_launcher_waits_for_the_hologram_mounted_after_an_await_and_a_pinger_for_the_charging_system()
        {
            // DeployProbesSystem makes a probe with its hologram's asset and both orders on it.
            // LaunchProbesSystem needs the Hologram, which HoloEmitter mounts once HoloProjector's
            // await is over; PingProbesSystem needs the Charge, which ChargeProbesSystem adds —
            // the next frame, since it runs before the probe is deployed.
            var output = Queries.Flow("DeployProbesSystem");

            Lines(output).Should().ContainInOrder(
                "DeployProbesSystem  #16 (Probes) [+0]  Systems/ProbeSystems.cs:20",
                "creates LaunchDue  [request]  ProbeSystems.cs:27",
                "read+removed by LaunchProbesSystem  #19 (Probes)  frame unknown (waits for Hologram, added after an await)  ProbeSystems.cs:65",
                "creates PingDue  [request]  ProbeSystems.cs:28",
                "read+removed by PingProbesSystem  #20 (Probes)  next frame (waits for Charge from ChargeProbesSystem #15) [+1]  ProbeSystems.cs:83");

            // A probe deployed with its Hologram is launched by order alone, the same frame.
            Lines(Queries.Flow("RedeployProbesSystem")).Should().Contain(
                "read+removed by LaunchProbesSystem  #19 (Probes)  same frame [+0]  ProbeSystems.cs:65");

            // Recorded on the writes in graph.json.
            string Waits(string system, string order) => string.Join("; ", Edges(system, Relation.Creates, order).Single().Waits
                .Select(w => $"{Label(w.System)} needs {Label(w.Needs)}: {w.Added ?? "by " + string.Join(", ", w.AddedBy.Select(Label))}"));
            Waits("DeployProbesSystem", "LaunchDue").Should().Be("LaunchProbesSystem needs Hologram: after an await");
            Waits("DeployProbesSystem", "PingDue").Should().Be("PingProbesSystem needs Charge: by ChargeProbesSystem");
            Waits("RedeployProbesSystem", "LaunchDue").Should().BeEmpty();
        }

        [Fact]
        public void Over_the_budget_a_flow_drops_what_it_does_not_follow_before_it_folds_its_endings()
        {
            var full = Queries.Flow("DockingOrder");
            Lines(full).Should().Contain("also, not followed: creates Docked, Pulse*");

            // At its own size it passes the headroom: the "also" lines go, nothing else.
            var atBudget = new GraphQueries(Graph, budget: full.Length / 4).Flow("DockingOrder");
            Lines(atBudget).Should().Equal(Lines(full).Where(l => !l.StartsWith("also, not followed", StringComparison.Ordinal)));

            // Only when that is still too much do the takers ending there share a line.
            var folded = new GraphQueries(Graph, budget: atBudget.Length / 4).Flow("DockingOrder");
            folded.Should().NotContain("also, not followed").And.NotContain("not shown");
            Lines(folded).Should().Contain("read by, ending there: GreetCaptainSystem #8, CountShipsDockedSystem #11 — next frame (waits for Armed) [+1]");

            // With room to spare it stays whole.
            new GraphQueries(Graph, budget: full.Length).Flow("DockingOrder").Should().Be(full);
        }

        [Fact]
        public void Orders_are_request_like_pulsed_news_event_like_and_long_lived_components_neither()
        {
            var messages = Graph.Nodes.Where(n => n.Message != null).ToDictionary(n => n.Label, n => n.Message);
            messages.Should().BeEquivalentTo(new Dictionary<string, string>
            {
                ["DockingOrder"] = "request", ["RefuelOrder"] = "request", ["SaleOrder"] = "request", ["ScrapFleetOrder"] = "request",
                ["RepaintOrder"] = "request", ["PilotOrder"] = "request", ["LaunchDue"] = "request", ["PingDue"] = "request", ["GangwayCall"] = "request",
                ["ShipDocked"] = "event", ["CargoUnloaded"] = "event"
            });

            // Who consumes a request is the reader whose loop destroys it, or marks it Doomed.
            Node("DockingOrder").ConsumedBy.Select(Label).Should().Equal("DockShipsSystem");
            Node("RefuelOrder").ConsumedBy.Select(Label).Should().Equal("RefuelShipsSystem");
            Node("CargoUnloaded").CarriedBy.Select(Label).Should().Equal("Pulse");
            Node("ShipDocked").CarriedBy.Select(Label).Should().Equal("Pulse");

            // A ship is doomed only when its hull is gone, destroyed by id when sold, doomed in a loop
            // nested in the scrap order's: it lives on otherwise. The plumbing is no message either.
            foreach (var name in new[] { "Ship", "ShipId", "Hull", "Fuel", "Cargo", "Wrecked", "Fresh", "Registered", "Pulse", "Armed", "Doomed", "Clock" })
                Node(name).MessageRole.Should().BeNull(name + " is no message");
        }

        [Fact]
        public void An_order_put_on_a_ship_is_a_request_its_handler_takes_off()
        {
            // The panel puts RepaintOrder on a ship that is already there; RepaintHullsSystem
            // paints the hull and takes the order off every ship it visits. The ship lives on.
            var repaint = Node("RepaintOrder");
            repaint.Message.Should().Be("request");
            repaint.ConsumedBy.Select(Label).Should().Equal("RepaintHullsSystem");
            repaint.ConsumedByRemoval.Should().BeTrue();
            Node("DockingOrder").ConsumedByRemoval.Should().BeFalse("its handler destroys it");

            Queries.Explain("RepaintOrder").Should().Contain(
                "  role: request (by structure: consumed by RepaintHullsSystem, which takes it off the entity)\n");
            Graph.ToJson().Should().Contain(
                "\"message\": \"request\",\n      \"consumed_by\": [\n        \"system:Harbor.RepaintHullsSystem\"\n      ],\n      \"consumed_by_removal\": true");
        }

        [Fact]
        public void Taking_a_component_off_is_no_handling_unless_the_loop_does_something_else_for_it()
        {
            // ForgetFreshSystem only takes Fresh off; AnswerHailsSystem only clears Hailed;
            // LogSightingsSystem sets nothing but the Logged its own group skips.
            Node("Fresh").Message.Should().BeNull("ForgetFreshSystem only unmarks");
            Node("Hailed").Message.Should().BeNull("AnswerHailsSystem clears it and writes nothing else");
            Node("Sighted").Message.Should().BeNull("a \"done\" flag the group skips is no work");

            // These two repaint, but are no handlers either: a salute is up for the whole frame and
            // taken down in Cleanup; Colours is struck because Priority, which HoistColoursSystem
            // requires, went — it follows Priority.
            Node("Salute").Message.Should().BeNull("ReturnSalutesSystem runs in Cleanup");
            Node("Colours").Message.Should().BeNull("StrikeColoursSystem skips what the setter requires");

            // SurveyHullsSystem repaints too, but marks the hulls itself: nobody hands it SurveyDue.
            Node("SurveyDue").Message.Should().BeNull("only its handler sets it");

            // Destroyed only after `if (!_signal.hasClock) return;`: the order may wait, and the
            // structure leaves it alone; the name finds it (Suffixes_that_name_…).
            Node("TowOrder").Message.Should().BeNull("TowHullsSystem destroys it only once the clock runs");
        }

        [Fact]
        public void A_jump_to_the_order_s_Destroy_skips_nothing_and_a_beacon_doused_at_the_frame_s_end_is_no_request()
        {
            // `if (ship == null || !ship.hasHull) goto done;` jumps over the work, onto `done: order.Destroy();`.
            Node("PilotOrder").Message.Should().Be("request");
            Node("PilotOrder").ConsumedBy.Select(Label).Should().Equal("PilotShipsSystem");
            Edges("PilotShipsSystem", Relation.Destroys, "PilotOrder").Should().ContainSingle(e => e.Condition == null);
            Edges("PilotShipsSystem", Relation.Creates, "Hull").Should().ContainSingle(e => e.Condition == "!(ship == null || !ship.hasHull)");

            // Lit every frame, watched, doused in Cleanup by a system doing nothing else: it was
            // up the whole frame for whoever watches it, a signal rather than a request.
            Node("Beacon").Message.Should().BeNull("only DouseBeaconsSystem destroys it, in Cleanup, and does nothing else");
        }

        [Fact]
        public void An_order_destroyed_only_once_its_ship_shows_a_hologram_waits_and_what_follows_has_no_count()
        {
            // BeamToShipsSystem destroys its order only `if (ship != null && ship.hasHologram)`,
            // and the projector mounts a Hologram only after its await: the order sits there.
            Node("Hologram").AddedLate.Should().Be("after an await", "RedeployProbesSystem adds one too, but on a probe it is making");
            Node("Charge").AddedLate.Should().BeNull("ChargeProbesSystem adds it the ordinary way");
            Node("Lens").AddedLate.Should().Be("after an await", "IFitting.Fit lands in LensFitting through the abstract Fitting");
            Edges("ProjectHologramsSystem", Relation.Creates, "Lens").Should().ContainSingle(e => e.Confidence == Confidence.Inferred,
                "the call graph follows IFitting.Fit through the abstract Fitting to LensFitting's override too");

            var lines = Lines(Queries.Flow("BeamOrder"));
            lines.Should().Contain(l => l.StartsWith("read+destroyed by BeamToShipsSystem", StringComparison.Ordinal) &&
                                        l.Contains("[+0], destroys it only once ship != null && ship.hasHologram (Hologram is added only after an await)  "));
            // What it hands on happens the frame the ship has one, which no order tells: no [+N].
            lines.Should().Contain(l => l.StartsWith("read+removed by RepaintHullsSystem", StringComparison.Ordinal) && !l.Contains("[+"));
            Queries.Flow("DockingOrder").Should().NotContain("only once", "DockShipsSystem destroys every order");
            Queries.Flow("CustomsCheck").Should().Contain("read+destroyed by ClearCustomsSystem")
                .And.NotContain("only once", "the Seal it asks for is one it puts on every check itself");
        }

        [Fact]
        public void A_call_skipped_by_a_jump_nested_in_an_if_waits_and_its_destroy_says_so()
        {
            // `if (ship != null) { if (ship.isWrecked) continue; … } call.Destroy();`: the call is
            // destroyed unless both hold; the Ballast is put inside the if, where the ship is not wrecked.
            Edges("BunkerHullsSystem", Relation.Destroys, "BunkerCall").Should().ContainSingle(e => e.Condition == "!(ship != null && ship.isWrecked)");
            Edges("BunkerHullsSystem", Relation.Creates, "Ballast").Should().ContainSingle(e => e.Condition == "ship != null && !ship.isWrecked");
            Lines(Queries.Flow("BunkerCall")).Should().Contain(l => l.StartsWith("read+destroyed by BunkerHullsSystem", StringComparison.Ordinal) &&
                                                                  l.Contains(", destroys it only once !(ship != null && ship.isWrecked)  "));

            // Turned down on the way to the nested `continue` (`ship != null && !ship.isDocked`),
            // answered after it (`!(ship != null && !ship.isDocked)`): every call goes.
            Edges("LowerGangwaysSystem", Relation.Destroys, "GangwayCall").Should().ContainSingle(e => e.Condition == null);
            Queries.Flow("GangwayCall").Should().Contain("read+destroyed by LowerGangwaysSystem").And.NotContain("only once");
            Node("GangwayCall").ConsumedBy.Select(Label).Should().Equal("LowerGangwaysSystem");
            Node("BunkerCall").Message.Should().BeNull("a wrecked ship's call stays");
        }

        [Fact]
        public void A_jump_nested_where_it_cannot_be_read_says_nothing_and_one_in_an_else_says_both_sides()
        {
            // A `continue` of a loop of its own; jumps under a switch and a try; a goto landing
            // before the write, which the `continue` next to it then says nothing about.
            Edges("MoorHullsSystem", Relation.Creates, "Cable").Should().ContainSingle(e => e.Condition == null);
            Edges("MoorHullsSystem", Relation.Creates, "Keel").Should().ContainSingle(e => e.Condition == null);
            Edges("MoorHullsSystem", Relation.Creates, "Ballast").Should().ContainSingle(e => e.Condition == null);

            // `if (mooring.hasCable) {…} else { if (mooring.hasBilge) continue; }`
            Edges("MoorHullsSystem", Relation.Creates, "Bilge").Should().ContainSingle(e => e.Condition == "!(!mooring.hasCable && mooring.hasBilge)");

            // Nested at the top of Execute, it says when the system runs at all: no condition on
            // the destroy, but the loop does not run every time, so the calls may wait.
            Edges("HaulKeelsSystem", Relation.Destroys, "KeelCall").Should().ContainSingle(e => e.Condition == null);
            Node("KeelCall").Message.Should().BeNull("HaulKeelsSystem destroys the calls only on odd ticks");
        }

        [Fact]
        public void A_constant_argument_decides_a_helper_s_nested_jump()
        {
            // Quays.Secure: `if (mooring.hasCable) { if (slack) return; } mooring.ReplaceBollard(1);`
            // false for slack decides the clause; true leaves it to the cable, so it stays.
            Edges("Quays", Relation.Creates, "Bollard").Should().ContainSingle(e => e.Condition == "!(mooring.hasCable && slack)");
            Edges("MoorHullsSystem", Relation.Creates, "Bollard").Should().ContainSingle(e => e.Through == "Quays.Secure" && e.Condition == null,
                "slack: false never returns there");
            Edges("SlackenMooringsSystem", Relation.Creates, "Bollard").Should().ContainSingle(e => e.Through == "Quays.Secure" &&
                                                                                                    e.Condition == "!(mooring.hasCable && slack)");
        }

        [Fact]
        public void Flow_does_not_list_again_the_removal_its_taker_line_already_names()
        {
            var flow = Queries.Flow("RepaintOrder");

            flow.Should().Contain("read+removed by RepaintHullsSystem");
            flow.Should().NotContain("removes RepaintOrder");
        }

        [Fact]
        public void Destroys_are_inferred_through_the_doom_marker_and_the_armed_pulse()
        {
            Edges("SweepDoomedSystem", Relation.Destroys, "RefuelOrder")
                .Should().ContainSingle(e => e.Confidence == Confidence.Inferred && e.Through == "created with RefuelOrder; Doomed by RefuelShipsSystem");
            Edges("SweepDoomedSystem", Relation.Destroys, "Wrecked")
                .Should().ContainSingle(e => e.Through == "marked Doomed by ScrapShipsSystem together with Wrecked");
            Edges("SweepPulsesSystem", Relation.Destroys, "ShipDocked")
                .Should().ContainSingle(e => e.Through == "created with Pulse + ShipDocked; Armed by ArmPulsesSystem");
            Edges("SweepPulsesSystem", Relation.Destroys, "CargoUnloaded")
                .Should().ContainSingle(e => e.Through == "created with CargoUnloaded + Pulse; Armed by ArmPulsesSystem");
        }

        [Fact]
        public void The_report_lists_a_flag_set_only_on_fresh_ships_as_set_once_and_counts_the_messages()
        {
            var report = GraphReport.Build(Graph);

            Section(report, "Flags set once on purpose?").Should().Contain(
                "- `Registered` set by `RegisterShipsSystem` — the setter only sees entities while they have `Fresh`, which `ForgetFreshSystem` removes");
            Section(report, "State flags that are never cleared").Should().Contain("None.");
            report.Should().Contain("- messages: 0 requests by name (`*Request`), 9 by structure, 0 of them both; 0 events by name (`*Event`), 2 by structure, 0 of them both");
            report.Should().NotContain("JustCreated").And.NotContain("GameCleanupSystems");
            Section(report, "Requests without a handler").Should().Contain("go by name (`--request-suffix`, `--event-suffix`)");
        }

        [Fact]
        public void A_helper_s_body_decides_what_its_lambda_runs_under_and_a_private_method_its_callers_do()
        {
            string? Condition(string component) => Edges("PrioritizeShipsSystem", Relation.Creates, component).Single().Condition;

            Condition("Priority").Should().Be("ship.cargo.Value > 100", "Apply runs it `if (gate)`; `gate` is no name the tool knew");
            Condition("Inspected").Should().Be("!ship.isPriority", "Unless returns early `if (skip)`, so it runs when skip is false");
            Condition("Expedited").Should().Be("ship.fuel.Value < 10", "Expedite is private and called only under that if");
        }

        [Fact]
        public void Only_the_launcher_passing_the_tanker_kind_creates_a_tank()
        {
            // ShipFactory.Launch adds Tank in `case ShipKind.Tanker`; one launcher passes Freighter, the other Tanker.
            Edges("ShipFactory", Relation.Creates, "Tank").Should().ContainSingle(e => e.Condition == "case Tanker");
            Edges("LaunchTankersSystem", Relation.Creates, "Tank").Should().ContainSingle(e =>
                e.Confidence == Confidence.Inferred && e.Through == "ShipFactory.Launch" && e.Condition == "_signal.hasClock && _signal.clock.Value % 25 == 0");
            Edges("LaunchShipsSystem", Relation.Creates, "Tank").Should().BeEmpty("it launches freighters only");

            // Everything else the factory makes, both make, under their own call's condition alone.
            Edges("LaunchShipsSystem", Relation.Creates, "Hull").Should().ContainSingle(e => e.Condition == "_signal.hasClock && _signal.clock.Value % 10 == 0");
            Edges("LaunchTankersSystem", Relation.Creates, "Hull").Should().ContainSingle();
            Queries.WhoCreates("Tank").Should().NotContain("LaunchShipsSystem");
        }

        [Fact]
        public void Explain_names_the_other_reader_of_an_order_and_where_each_role_comes_from()
        {
            Queries.Explain("DockShipsSystem").Should().Contain(
                "  its requests are also read by:\n    DockingOrder → LogOrdersSystem (Berthing, CoreLoop #4)\n");
            Queries.Explain("DockingOrder").Should().Contain("  role: request (by structure: consumed by DockShipsSystem)\n");
            Queries.Explain("CargoUnloaded").Should().Contain("  role: event (by structure: carried by Pulse)\n");
        }

        [Fact]
        public void A_reader_named_after_a_structural_event_takes_no_seed()
        {
            var output = Queries.Query("ship docked");
            var head = output.Split('\n')[0];

            // CountShipsDockedSystem says nothing ShipDocked does not: the event shows it one step out.
            head.Should().StartWith("query \"ship docked\": 18 nodes match; seeds: DockShipsSystem, ShipDocked, Ship, Docked, DockingPanel (also: CountShipsDockedSystem, ");
            Sections(output)["1 step out:"].Should().Contain("CountShipsDockedSystem");

            // It is the structure that makes ShipDocked an event here: without it the reader seeds.
            var unrecognised = EcsGraph.FromJson(Graph.ToJson());
            unrecognised.Nodes.Single(n => n.Label == "ShipDocked").Message = null;
            new GraphQueries(unrecognised, budget: 0).Query("ship docked").Split('\n')[0].Should().Contain("seeds: DockShipsSystem, ShipDocked, CountShipsDockedSystem, ");
        }

        [Fact]
        public void Roots_and_execution_order_are_found_without_names()
        {
            Graph.Nodes.Where(n => n.Root).Select(n => n.Label).Should().BeEquivalentTo("CoreLoop", "LateLoop", "DebugLoop");

            var placement = Node("DockShipsSystem").Placements.Single();
            Label(placement.Root).Should().Be("CoreLoop");
            placement.Order.Should().Be(5, "ArmPulsesSystem, TickClockSystem, then Berthing's IssueDockingOrdersSystem, LogOrdersSystem and this");
            placement.Chain.Select(Label).Should().Equal("CoreLoop", "Berthing");
            Node("LogOrdersSystem").Placements.Select(p => Label(p.Root)).Should().BeEquivalentTo("CoreLoop", "DebugLoop");
            Node("RemoveFuelChangedPortSystem").Placements.Single().Chain.Select(Label).Should().Equal("CoreLoop", "PortWatchedCleanupSystems");
        }

        [Fact]
        public void The_graph_does_not_depend_on_the_order_the_sources_come_in()
        {
            static string Json(Compilation compilation)
            {
                var graph = GraphBuilder.Build(compilation, FixtureFolder.Neutral.Root, new GraphOptions());
                graph.Metadata.BuildMilliseconds = 0;
                return graph.ToJson();
            }

            var compilation = FixtureFolder.Neutral.Compile();
            var reversed = CSharpCompilation.Create("Harbor", compilation.SyntaxTrees.Reverse(), FixtureProject.References(),
                new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));

            Json(reversed).Should().Be(Json(compilation));
        }

        [Fact]
        public void File_mode_gives_the_same_graph()
        {
            Signature(FixtureFolder.Neutral.Build(filesMode: true)).Should().BeEquivalentTo(Signature(Graph));
        }

        [Fact]
        public void Graph_json_carries_the_structure_and_round_trips()
        {
            var json = Graph.ToJson();

            EcsGraph.FromJson(json).ToJson().Should().Be(json);
            json.Should().Contain("\"message\": \"request\",\n      \"consumed_by\": [\n        \"system:Harbor.DockShipsSystem\"\n      ]");
            json.Should().Contain("\"message\": \"event\",\n      \"carried_by\": [\n        \"component:Pulse\"\n      ]");
            json.Should().Contain("\"unseen_by\": [\n        {\n          \"system\": \"system:Harbor.WarnLowFuelSystem\",\n          \"needs\": \"component:Ship\"\n        }\n      ]");
            json.Should().Contain("\"may_miss\": [\n        {\n          \"system\": \"system:Harbor.AlignLodestonesSystem\",\n          \"needs\": \"component:Lodestone\"\n        }\n      ]");
        }

        [Fact]
        public void Suffixes_that_name_the_port_s_messages_agree_with_the_structure()
        {
            // What a project gets when it tells the tool its convention: orders end in "Order",
            // news in "Docked". The names then decide, and decide the same.
            var named = FixtureFolder.Neutral.Build(options: new GraphOptions { RequestSuffix = "Order", EventSuffix = "Docked" });

            named.Nodes.Where(n => n.Role != null).Select(n => (n.Label, n.Role))
                .Should().BeEquivalentTo(new[]
                {
                    ("DockingOrder", "request"), ("RefuelOrder", "request"), ("SaleOrder", "request"), ("ScrapFleetOrder", "request"),
                    ("RepaintOrder", "request"), ("TowOrder", "request"), ("PilotOrder", "request"), ("BeamOrder", "request"), ("ShipDocked", "event")
                });
            named.Nodes.Single(n => n.Label == "CargoUnloaded").MessageRole.Should().Be("event", "no suffix names it; the structure still does");

            // But for two: TowOrder is destroyed only once the clock runs, BeamOrder only once its
            // ship shows a hologram, which the structure leaves alone on purpose — the name finds them.
            named.Nodes.Where(n => n.Role != null && n.Message != n.Role).Select(n => n.Label).Should().Equal("BeamOrder", "TowOrder");
            named.Nodes.Single(n => n.Label == "TowOrder").MessageRole.Should().Be("request");
            GraphReport.Build(named).Should().Contain("  - 2 of the requests are found by name only: ")
                .And.Contain("so keep `--request-suffix`: `BeamOrder`, `TowOrder`\n");

            var byName = new GraphQueries(named, budget: 0);
            byName.Flow("DockingOrder").Should().Be(Queries.Flow("DockingOrder"));
            byName.Flow("RefuelOrder").Should().Be(Queries.Flow("RefuelOrder"));
            byName.Explain("DockShipsSystem").Should().Be(Queries.Explain("DockShipsSystem"));
        }

        // -- reading an answer -----------------------------------------------------------------

        static IEnumerable<string> Signature(EcsGraph graph) =>
            graph.Edges.Select(e => $"{e.Source} {e.Relation} {e.Target} {e.Via} {e.Confidence} {e.Through} {e.Called} {e.Entity} {e.Order} {e.OnlyWhile} {e.Condition}")
                .Concat(graph.Nodes.Select(n => $"{n.Id} {n.Kind} {n.Root} {string.Join(",", n.Placements.Select(p => p.Root + p.Order))} " +
                                                $"{string.Join(",", n.TransientRemovedBy)} {n.Message} {string.Join(",", n.ConsumedBy.Concat(n.CarriedBy))}"));

        static List<string> Lines(string output) => output.Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0).ToList();

        static string Section(string report, string title)
        {
            var start = report.IndexOf("### " + title, StringComparison.Ordinal);
            start.Should().BeGreaterThan(0, $"the report has a '{title}' section");
            var end = report.IndexOf("\n### ", start + 4, StringComparison.Ordinal);
            return end < 0 ? report.Substring(start) : report.Substring(start, end - start);
        }

        /// <summary>A query answer's sections ("Seeds:", "1 step out:", …) and the nodes listed under each.</summary>
        static Dictionary<string, List<string>> Sections(string output)
        {
            var sections = new Dictionary<string, List<string>>();
            List<string>? current = null;
            foreach (var line in output.Split('\n'))
            {
                if (line.EndsWith(":", StringComparison.Ordinal) && !line.StartsWith(" ", StringComparison.Ordinal))
                    sections[line] = current = new List<string>();
                else if (current != null && line.StartsWith("  ", StringComparison.Ordinal) && !line.StartsWith("    ", StringComparison.Ordinal))
                    current.Add(line.Trim().Split(' ')[0]);
            }
            return sections;
        }
    }
}
