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

            output.Should().StartWith("GameplayFeature (root) — 11 systems");
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
            output.Should().Contain("destroys (3):");
        }

        [Fact]
        public void Query_ranks_by_how_many_words_match()
        {
            var output = Queries.Query("furniture request", 3);

            var best = output.Split('\n')[1];
            (best.Contains("PlaceFurnitureRequest") || best.Contains("PlaceFurnitureByRequestSystem")).Should().BeTrue(best);
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
    }
}
