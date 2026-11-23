using System;
using System.Linq;
using Entitas.Graph.Output;
using FluentAssertions;
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

            output.Should().StartWith("GameplayFeature (root) — 20 systems");
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
            output.Should().Contain("destroys (4):");
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
            tiny.SystemsOf("GameplayFeature").Should().Contain("nested features, largest: ShopFeature 9, FurnitureFeature 8, GameCleanupSystems 1");
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
        public void Graph_json_round_trips_byte_for_byte()
        {
            var json = FixtureProject.Graph.ToJson();

            EcsGraph.FromJson(json).ToJson().Should().Be(json);
            json.Should().Contain("\"directed\": true").And.Contain("\"schema_version\": 1");
            json.Should().Contain("\"relation\": \"creates\"").And.Contain("\"source_location\": \"L");
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

            html.Should().Contain("<div id=\"legend\"></div>").And.Contain("Arrows follow the data")
                .And.Contain("REVERSED.has(r) ? 'component → system' : 'system → component'", "the legend gives each relation's direction");
        }
    }
}
