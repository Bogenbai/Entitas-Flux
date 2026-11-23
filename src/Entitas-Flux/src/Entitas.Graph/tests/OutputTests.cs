using System;
using System.Linq;
using Entitas.Graph.Analysis;
using Entitas.Graph.Output;
using FluentAssertions;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Xunit;

namespace Entitas.Graph.Tests
{
    /// <summary>The commands, the report, graph.json and graph.html, on the Fixture/ project.</summary>
    public class OutputTests
    {
        static readonly GraphQueries Queries = new GraphQueries(FixtureProject.Graph);

        [Fact]
        public void Who_creates_lists_direct_and_inferred_creators_with_their_place()
        {
            var output = Queries.WhoCreates("Furniture");

            output.Should().StartWith("Furniture — created by 2 (1 direct, 1 inferred)");
            output.Should().Contain("PlaceFurnitureByRequestSystem  [Flag*]  FurnitureFeature #2, GameplayFeature order 3");
            output.Should().Contain("(FurnitureFactory.CreateFurniture)");
            output.Should().Contain("FurnitureFactory  [Flag]  [type]");
        }

        [Fact]
        public void Who_reads_includes_reactions_and_filters()
        {
            var output = Queries.WhoReads("Selected");

            output.Should().Contain("HighlightOnSelectedSystem").And.Contain("reacts via AllOf");
            output.Should().Contain("SelectSystem").And.Contain("filters via NoneOf");
        }

        [Fact]
        public void Lifecycle_orders_everything_by_execution_per_root()
        {
            var output = Queries.Lifecycle("FurniturePlacedEvent");
            var lines = output.Split('\n').Select(l => l.Trim()).ToList();

            output.Should().Contain("created by 1 · read by 1 · filtered by 0 · removed by 0 · destroyed by 1");
            lines.Should().ContainInOrder(
                "GameplayFeature:",
                "#3    creates                PlaceFurnitureByRequestSystem  (FurnitureFeature)",
                "#10   reads                  UnlockAchievementSystem  (GameplayFeature)");
            output.Should().Contain("LateFeature:").And.Contain("destroys               DestroyEventsSystem  (LateFeature)  — via created with Event + FurniturePlacedEvent");
        }

        [Fact]
        public void Systems_of_expands_nested_features_in_order()
        {
            var output = Queries.SystemsOf("GameplayFeature");

            output.Should().StartWith("GameplayFeature (root) — 24 systems");
            output.Split('\n').Select(l => l.Trim()).Should().ContainInOrder(
                "1. MarkEventsReadySystem  [Execute]",
                "2. FurnitureFeature (feature)",
                "1. RequestPlacementSystem  [Execute]",
                "3. UnlockAchievementSystem  [Execute]",
                "4. GameCleanupSystems (feature)");
        }

        [Fact]
        public void Path_follows_data_flow_not_shared_types()
        {
            var output = Queries.Path("RequestPlacementSystem", "UnlockAchievementSystem");

            output.Should().StartWith("RequestPlacementSystem → UnlockAchievementSystem: 4 hops");
            output.Should().Contain("RequestPlacementSystem --creates--> PlaceFurnitureRequest");
            output.Should().Contain("PlaceFurnitureRequest --read by--> PlaceFurnitureByRequestSystem");
            output.Should().Contain("PlaceFurnitureByRequestSystem --creates--> FurniturePlacedEvent");
            output.Should().Contain("FurniturePlacedEvent --read by--> UnlockAchievementSystem");
        }

        [Fact]
        public void Explain_shows_placement_and_every_connection()
        {
            var output = Queries.Explain("DestroyEventsSystem");

            output.Should().Contain("registered: LateFeature #1");
            output.Should().Contain("runs under LateFeature at #1 via LateFeature");
            output.Should().Contain("destroys (7):");
        }

        [Fact]
        public void An_answer_over_the_budget_starts_with_a_summary_and_says_what_it_left_out()
        {
            var full = Queries.WhoReads("Selected");
            var capped = new GraphQueries(FixtureProject.Graph, budget: 40).WhoReads("Selected");

            capped.Should().StartWith("Selected — read by 4");
            capped.Should().Contain("  by feature: FurnitureFeature 3, (unregistered) 1");
            capped.Should().Contain("more systems and types not shown — the answer is capped at ~40 tokens (--budget 0 prints everything).");
            capped.Length.Should().BeLessThan(full.Length);
            full.Should().NotContain("not shown", "the default budget fits every fixture answer");
        }

        [Fact]
        public void Every_command_summarises_its_own_shape_when_cut()
        {
            var tiny = new GraphQueries(FixtureProject.Graph, budget: 10);

            tiny.Lifecycle("Destructed").Should().Contain("  steps by root: GameplayFeature").And.Contain("  by feature: ");
            tiny.SystemsOf("GameplayFeature").Should().Contain("nested features, largest: ShopFeature 13, FurnitureFeature 8, GameCleanupSystems 1");
            tiny.Explain("PlaceFurnitureByRequestSystem").Should().Contain("  connections: creates 6, reads 2, registered by 1");
            tiny.Query("furniture").Should().Contain("; subgraph: ").And.Contain("more nodes not shown");
        }

        [Fact]
        public void A_budget_of_zero_prints_everything()
        {
            var unlimited = new GraphQueries(FixtureProject.Graph, budget: 0);

            unlimited.Lifecycle("FurniturePlacedEvent").Should().Be(Queries.Lifecycle("FurniturePlacedEvent"));
            unlimited.WhoReads("Selected").Should().Be(Queries.WhoReads("Selected"));
            unlimited.SystemsOf("GameplayFeature").Should().NotContain("not shown");
            unlimited.Query("furniture request").Should().Be(Queries.Query("furniture request"));
        }

        [Fact]
        public void Systems_in_execution_order_come_before_types_outside_features()
        {
            var lines = Queries.WhoCreates("Furniture").Split('\n').Select(l => l.Trim()).ToList();

            lines.Should().ContainInOrder(
                "PlaceFurnitureByRequestSystem  [Flag*]  FurnitureFeature #2, GameplayFeature order 3",
                "FurnitureFactory  [Flag]  [type]");
        }

        [Fact]
        public void Explain_names_the_other_readers_of_the_requests_a_system_handles()
        {
            // The shape that hid the floating "+coins" text on a real project: a second reader of
            // the request, outside everything the handler itself creates.
            var graph = new EcsGraph();
            void Node(string kind, string name, string? role = null) =>
                graph.Nodes.Add(new GraphNode { Id = name, Label = name, Kind = kind, FullName = "Game." + name, Role = role });
            void Edge(string source, string relation, string target) =>
                graph.Edges.Add(new GraphEdge { Source = source, Target = target, Relation = relation, Via = relation == Relation.Reads ? "AllOf" : "Add", SourceFile = "Code.cs", Line = graph.Edges.Count + 1 });

            Node(NodeKind.Component, "ChangeCurrencyRequest", "request");
            Node(NodeKind.Component, "CoinsSpentEvent", "event");
            foreach (var system in new[] { "ChangeCurrencyByRequestSystem", "PlayTextOnCurrencyChangeByRequestSystem", "DestroyChangeCurrencyRequestsSystem", "PayForOrdersSystem" })
                Node(NodeKind.System, system);
            Edge("PayForOrdersSystem", Relation.Creates, "ChangeCurrencyRequest");
            Edge("ChangeCurrencyByRequestSystem", Relation.Reads, "ChangeCurrencyRequest");
            Edge("ChangeCurrencyByRequestSystem", Relation.Creates, "CoinsSpentEvent");
            Edge("PlayTextOnCurrencyChangeByRequestSystem", Relation.Reads, "ChangeCurrencyRequest");
            Edge("DestroyChangeCurrencyRequestsSystem", Relation.Reads, "ChangeCurrencyRequest");
            Edge("DestroyChangeCurrencyRequestsSystem", Relation.Destroys, "ChangeCurrencyRequest");
            var queries = new GraphQueries(graph);

            queries.Explain("ChangeCurrencyByRequestSystem").Should().Contain(
                "  its requests are also read by:\n    ChangeCurrencyRequest → DestroyChangeCurrencyRequestsSystem (unregistered), PlayTextOnCurrencyChangeByRequestSystem (unregistered)\n");
            queries.Explain("PayForOrdersSystem").Should().NotContain("also read by", "it creates the request, it does not handle it");
            queries.Explain("ChangeCurrencyRequest").Should().NotContain("also read by", "a component lists its readers anyway");
        }

        [Fact]
        public void Names_are_never_guessed()
        {
            Action ambiguous = () => Queries.Resolve("Sample");
            Action missing = () => Queries.Resolve("Furnitur");

            missing.Should().Throw<NodeNotFoundException>().Which.Suggestions.Select(s => s.Label).Should().Contain("Furniture");
            Queries.Resolve("FurnitureComponent").Label.Should().Be("Furniture", "the Component suffix is optional");
            ambiguous.Should().Throw<NodeNotFoundException>();
        }

        [Fact]
        public void The_report_lists_the_fixture_findings_and_nothing_else()
        {
            var report = GraphReport.Build(FixtureProject.Graph);

            Section(report, "Components nobody reads").Should().Contain("`Unused`").And.Contain("`OrphanRequest`");
            Section(report, "Components read but never created").Should().Contain("`Dead`");
            Section(report, "Requests without a handler").Should().Contain("`OrphanRequest`");
            Section(report, "Requests nobody destroys or removes").Should().Contain("None.");
            Section(report, "Events without a consumer").Should().Contain("None.");
            Section(report, "Events destroyed by hand").Should().Contain("None.");
            Section(report, "[Watched] components nobody reacts to").Should().Contain("None.");
            Section(report, "State flags that are never cleared").Should().Contain("`Inspected`").And.NotContain("`Furniture`");
            Section(report, "Systems not registered in any feature").Should().Contain("`OrphanSystem`").And.Contain("`GameWatchedCleanupSystems` (generated feature)");
        }

        static string Section(string report, string title)
        {
            var start = report.IndexOf("### " + title, StringComparison.Ordinal);
            start.Should().BeGreaterThan(0, $"the report has a '{title}' section");
            var end = report.IndexOf("\n### ", start + 4, StringComparison.Ordinal);
            return end < 0 ? report.Substring(start) : report.Substring(start, end - start);
        }

        [Fact]
        public void The_graph_does_not_depend_on_the_order_the_sources_come_in()
        {
            // The trees are walked in parallel and the csproj lists files in whatever order Unity
            // writes it; neither may change graph.json. Reversing the compilation's trees shows it
            // on a small fixture, where a race between threads rarely does.
            static string Json(Compilation compilation)
            {
                var graph = GraphBuilder.Build(compilation, FixtureProject.Root, new GraphOptions());
                graph.Metadata.BuildMilliseconds = 0;
                return graph.ToJson();
            }

            var compilation = FixtureProject.Compile();
            var reversed = CSharpCompilation.Create("Fixture", compilation.SyntaxTrees.Reverse(), FixtureProject.References(),
                new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));

            Json(reversed).Should().Be(Json(compilation));
        }

        [Fact]
        public void Graph_json_round_trips_byte_for_byte()
        {
            var json = FixtureProject.Graph.ToJson();

            EcsGraph.FromJson(json).ToJson().Should().Be(json);
            json.Should().Contain("\"directed\": true").And.Contain("\"schema_version\": 1");
            json.Should().Contain("\"relation\": \"creates\"").And.Contain("\"source_location\": \"L");
        }

        [Fact]
        public void Conditions_and_called_methods_round_trip_and_are_left_out_when_absent()
        {
            var graph = new EcsGraph();
            graph.Nodes.Add(new GraphNode { Id = "system:S", Label = "S", Kind = NodeKind.System });
            graph.Nodes.Add(new GraphNode { Id = "component:X", Label = "X", Kind = NodeKind.Component });
            graph.Edges.Add(new GraphEdge
            {
                Source = "system:S", Target = "component:X", Relation = Relation.Creates, Via = "Add", Confidence = Confidence.Inferred,
                Through = "Moves.Stop", Called = "Factory.Make; Factory.Remake", Condition = "!(amount < 0) && … or case Kind.X",
                SourceFile = "Assets/Moves.cs", Line = 4
            });
            graph.Edges.Add(new GraphEdge { Source = "system:S", Target = "component:X", Relation = Relation.Reads, Via = "AllOf", SourceFile = "Assets/S.cs", Line = 9 });

            var json = graph.ToJson();
            var back = EcsGraph.FromJson(json);

            back.ToJson().Should().Be(json);
            back.Edges[0].Condition.Should().Be("!(amount < 0) && … or case Kind.X");
            back.Edges[0].Called.Should().Be("Factory.Make; Factory.Remake");
            json.Should().Contain("\"through\": \"Moves.Stop\",\n      \"called\": \"Factory.Make; Factory.Remake\"");
            back.Edges[1].Condition.Should().BeNull();
            json.Split("\"condition\"").Length.Should().Be(2, "an edge with no condition writes no field");

            // The fixture has both, and they survive a round trip byte for byte too.
            FixtureProject.Graph.ToJson().Should().Contain("\"condition\": \"till.Till < 0\"").And.Contain("\"called\": \"ShopkeeperFactory.Create\"");
        }

        [Fact]
        public void Graph_html_is_self_contained()
        {
            var html = GraphHtml.Build(FixtureProject.Graph);

            html.Should().Contain("vis.js is dual licensed", "vis-network is inlined with its licence banner");
            html.Should().Contain("const GRAPH = {").And.Contain("\"label\":\"FurniturePlacedEvent\"");
            html.Should().NotContain("unpkg.com").And.NotContain("cdn.jsdelivr");
            var start = html.IndexOf("const GRAPH = ", StringComparison.Ordinal);
            var end = html.IndexOf("</script>", start, StringComparison.Ordinal);
            html.Substring(start, end - start).Should().NotContain("</", "a '</' inside the data would close the script early");
        }

        [Fact]
        public void Graph_html_frames_a_found_node_after_the_layout_settles()
        {
            var html = GraphHtml.Build(FixtureProject.Graph);

            // render() restarts the physics, so framing only right after it aimed at where the
            // node was before the layout moved it; it must happen again once the layout is done.
            var settled = html.IndexOf("network.on('stabilizationIterationsDone'", StringComparison.Ordinal);
            settled.Should().BeGreaterThan(0);
            html.IndexOf("frame(id)", settled, StringComparison.Ordinal).Should().BeGreaterThan(settled);
            html.Should().Contain("network.getBoundingBox(n)", "a scope of one or two steps frames the node with its neighbours, labels included");
            html.Should().Contain("network.once('afterDrawing', () => frame(id))", "a label's size is known only once it has been drawn");
            html.Should().Contain("$('legend').offsetHeight", "in a narrow window the legend is a box over the canvas, so the frame leaves room for it");
            html.Should().Contain("document.addEventListener('visibilitychange'", "a page opened in a hidden tab draws nothing until it is shown");
        }

        [Fact]
        public void Graph_html_without_a_link_opens_on_the_overview()
        {
            var html = GraphHtml.Build(FixtureProject.Graph);

            // With no #node= the page picks a node to draw; the panel must say why that node,
            // not leave it looking like a link the reader followed.
            html.Should().Contain("if (!linked) showOverview(start);");
            html.Should().Contain("'busiest event'");
        }

        [Fact]
        public void Graph_html_carries_the_condition_of_a_conditional_write()
        {
            var html = GraphHtml.Build(FixtureProject.Graph);
            var start = html.IndexOf("const GRAPH = ", StringComparison.Ordinal) + "const GRAPH = ".Length;
            var end = html.IndexOf(";</script>", start, StringComparison.Ordinal);
            using var data = System.Text.Json.JsonDocument.Parse(html.Substring(start, end - start));

            var conditional = FixtureProject.Graph.Edges.Where(e => e.Condition != null).ToList();
            conditional.Should().NotBeEmpty("the fixture has writes under an if");
            data.RootElement.GetProperty("edges").EnumerateArray()
                .Count(e => e.TryGetProperty("condition", out _)).Should().Be(conditional.Count);
            html.Should().Contain("'<span class=\"cond\">if <code>' + escapeHtml(conditions)", "the side panel shows it");
        }

        [Fact]
        public void Graph_html_draws_reads_from_the_component_into_the_system()
        {
            var html = GraphHtml.Build(FixtureProject.Graph);
            var start = html.IndexOf("const GRAPH = ", StringComparison.Ordinal) + "const GRAPH = ".Length;
            var end = html.IndexOf(";</script>", start, StringComparison.Ordinal);
            using var data = System.Text.Json.JsonDocument.Parse(html.Substring(start, end - start));
            string Label(string id) => FixtureProject.Graph.Node(id)!.Label;

            // The data keeps the stored direction (the side panel reads it as "reads" / "read by")
            // and names the relations the page turns around: data flows from the component into
            // whoever reads, filters on or reacts to it.
            data.RootElement.GetProperty("reversed").EnumerateArray().Select(r => r.GetString())
                .Should().Equal(Relation.Reads, Relation.Filters, Relation.Reacts);
            data.RootElement.GetProperty("edges").EnumerateArray()
                .Where(e => e.GetProperty("relation").GetString() == Relation.Reads)
                .Select(e => (Label(e.GetProperty("from").GetString()!), Label(e.GetProperty("to").GetString()!)))
                .Should().Contain(("UnlockAchievementSystem", "FurniturePlacedEvent"));
            html.Should().Contain("const back = REVERSED.has(e.relation)")
                .And.Contain("from: back ? e.to : e.from, to: back ? e.from : e.to", "a reversed relation is drawn component → system");

            html.Should().Contain("id=\"legend\"").And.Contain("Arrows follow the data")
                .And.Contain("REVERSED.has(r) ? 'component → system' : 'system → component'", "the legend gives each relation's direction");
        }
    }
}
