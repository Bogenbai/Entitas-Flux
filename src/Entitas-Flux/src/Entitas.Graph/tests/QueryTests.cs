using System;
using System.Collections.Generic;
using System.Linq;
using Entitas.Graph.Output;
using FluentAssertions;
using Xunit;

namespace Entitas.Graph.Tests
{
    /// <summary>`query`: seeds, the walk around them, hubs, each edge once, the budget — on the Fixture/ project.</summary>
    public class QueryTests
    {
        static readonly GraphQueries Queries = new GraphQueries(FixtureProject.Graph);

        [Fact]
        public void Seeds_are_the_best_scored_nodes_ties_broken_by_written_edges()
        {
            var head = Queries.Query("furniture request").Split('\n')[0];

            // Both words: the system before the component (more edges). One word: Furniture says
            // nothing else; the rest say one more word each and go by the edges written in them.
            // CountFurnitureSystem reads Furniture only through IFurnitureQuery, and an inferred
            // edge breaks no tie, so FurnitureFactory goes first.
            head.Should().Be("query \"furniture request\": 10 nodes match; " +
                             "seeds: PlaceFurnitureByRequestSystem, PlaceFurnitureRequest, Furniture, RequestPlacementSystem, FurnitureFactory " +
                             "(also: FurnitureFeature, FurniturePlacedEvent, FurnitureQuery, CountFurnitureSystem, OrphanRequest)");
            Queries.Query("furniture request", limit: 2).Should().Contain("; seeds: PlaceFurnitureByRequestSystem, PlaceFurnitureRequest (also: ");
        }

        [Fact]
        public void Words_match_names_word_for_word_by_stem()
        {
            // "placement" is Place in PlaceFurnitureByRequestSystem and Placed in FurniturePlacedEvent:
            // as text it matched only RequestPlacementSystem.
            Queries.Query("furniture placement").Split('\n')[0].Should().StartWith(
                "query \"furniture placement\": 9 nodes match; seeds: PlaceFurnitureByRequestSystem, PlaceFurnitureRequest, FurniturePlacedEvent, RequestPlacementSystem, Furniture ");

            // The start of a word still finds it; the middle of one does not ("cat" found Indicator).
            Queries.Query("furn").Should().Contain("seeds: Furniture, PlaceFurnitureByRequestSystem, ");
            Action inside = () => Queries.Query("vent");
            inside.Should().Throw<NodeNotFoundException>().WithMessage("no node matches \"vent\"");

            // A question's own words match nothing: "on" would find every XOnY system.
            Queries.Query("how does furniture placement work").Should().StartWith("query \"how does furniture placement work\": 9 nodes match; seeds: PlaceFurnitureByRequestSystem, ");
        }

        [Fact]
        public void A_name_outranks_a_namespace_and_says_the_least_besides_the_words()
        {
            var head = new GraphQueries(PlacementFeature(), budget: 0).Query("furniture placement").Split('\n')[0];

            // Every Placement.* system has "placement" in its namespace; the system that places
            // furniture is in Furniture.* and creates 45 components through a factory.
            head.Should().StartWith("query \"furniture placement\": 13 nodes match; seeds: " +
                                    "PlaceFurnitureByRequestSystem, PlaceFurnitureRequest, FurniturePlacedEvent, " +
                                    "EndFurniturePlacementOnBuildModeStopSystem, StartReplacingFurnitureByRequestSystem " +
                                    "(also: StartReplacingFurnitureRequest, MoveFurnitureGhostSystem, ");
        }

        [Fact]
        public void A_feature_seeds_only_when_nothing_else_matches()
        {
            // FurnitureFeature outranks Furniture by degree, but it has no data flow to walk.
            Queries.Query("furniture request").Should().Contain("seeds: PlaceFurnitureByRequestSystem, PlaceFurnitureRequest, Furniture,");
            Queries.Query("LateFeature").Should().Contain("seeds: LateFeature").And.Contain("LateFeature  [feature; root]  (runs 2 systems — use systems-of LateFeature)");
        }

        [Fact]
        public void Depth_one_reaches_the_neighbours_and_depth_two_their_neighbours()
        {
            var one = Sections(Queries.Query("PlaceFurnitureRequest", depth: 1));
            var two = Sections(Queries.Query("PlaceFurnitureRequest", depth: 2));

            one["Seeds:"].Should().Equal("PlaceFurnitureRequest");
            one["1 step out:"].Should().BeEquivalentTo("PlaceFurnitureByRequestSystem", "RequestPlacementSystem", "DestroyDestructedSystem");
            one.Should().NotContainKey("2 steps out:");
            two["1 step out:"].Should().BeEquivalentTo(one["1 step out:"]);
            two["2 steps out:"].Should().Contain("FurniturePlacedEvent", "PlaceFurnitureByRequestSystem creates it")
                .And.Contain("Destructed").And.Contain("OrphanRequest");
        }

        [Fact]
        public void Registration_is_an_annotation_not_a_step()
        {
            var output = Queries.Query("PlaceFurnitureRequest", depth: 1);

            output.Should().Contain("PlaceFurnitureByRequestSystem  [system; FurnitureFeature #2, GameplayFeature order 3]");
            output.Should().NotContain("FurnitureFeature  [", "contains edges are not walked");
            output.Should().Contain("PlaceFurnitureRequest  [component; Game, data, request]");
        }

        [Fact]
        public void A_hub_is_reached_but_not_walked_through()
        {
            var output = new GraphQueries(FixtureProject.Graph, budget: 0, hubThreshold: 2).Query("PlaceFurnitureRequest", depth: 2);
            var sections = Sections(output);

            output.Should().Contain("PlaceFurnitureByRequestSystem  [system; FurnitureFeature #2, GameplayFeature order 3]  (hub: touches 3 components in its own code — not expanded; use explain)");
            sections["1 step out:"].Should().Contain("PlaceFurnitureByRequestSystem").And.Contain("RequestPlacementSystem").And.Contain("DestroyDestructedSystem");
            output.Should().NotContain("FurniturePlacedEvent", "it is reachable only through the hub");
            // RequestPlacementSystem writes three components itself: a hub at this threshold too.
            sections["2 steps out:"].Should().NotContain("OrphanRequest", "RequestPlacementSystem is a hub and is not walked through");
            // DestroyDestructedSystem destroys six components, but only Destructed in its own code;
            // the rest is inferred from what its entities are made of, so it is walked through.
            sections["2 steps out:"].Should().Contain("TimeIsUp").And.Contain("ProcessingStoring");
            new GraphQueries(FixtureProject.Graph, budget: 0, hubThreshold: 3).Query("DestroyDestructedSystem", depth: 1)
                .Should().Contain("Destructed  [component; Game, flag]  (hub: read by 3, created by 3, destroyed by 1 — not expanded; use who-reads/lifecycle)");
            Links(output).Should().NotContain(l => l.Node == "PlaceFurnitureByRequestSystem", "a hub lists nothing of its own");
        }

        [Fact]
        public void Components_a_factory_adds_do_not_make_a_hub()
        {
            // Seven neighbours, but Furniture, Health, Id and Event only through FurnitureFactory
            // and CreateEntity.Event: three written in the system itself.
            var output = new GraphQueries(FixtureProject.Graph, budget: 0, hubThreshold: 3).Query("PlaceFurnitureRequest", depth: 2);

            output.Should().NotContain("PlaceFurnitureByRequestSystem  [system; FurnitureFeature #2, GameplayFeature order 3]  (hub");
            Links(output).Should().Contain(("PlaceFurnitureByRequestSystem", "creates", "FurniturePlacedEvent"));
            Sections(output)["2 steps out:"].Should().Contain("FurniturePlacedEvent");

            // The same at scale and the default threshold: 49 neighbours, 45 of them through a factory.
            var placing = new GraphQueries(PlacementFeature(), budget: 0).Query("PlaceFurnitureRequest", depth: 2);
            placing.Should().NotContain("(hub");
            Links(placing).Should().Contain(("PlaceFurnitureByRequestSystem", "creates", "FurniturePlacedEvent"));
        }

        [Fact]
        public void A_seed_is_walked_through_however_many_neighbours_it_has()
        {
            var output = new GraphQueries(FixtureProject.Graph, budget: 0, hubThreshold: 2).Query("PlaceFurnitureByRequestSystem", depth: 1);

            output.Split('\n').Should().Contain("  PlaceFurnitureByRequestSystem  [system; FurnitureFeature #2, GameplayFeature order 3]  Systems/FurnitureSystems.cs:22");
            output.Should().Contain("    creates FurniturePlacedEvent  via Add  FurnitureSystems.cs:39");
        }

        [Fact]
        public void Each_edge_is_listed_once_from_the_side_nearer_the_seeds()
        {
            var unlimited = new GraphQueries(FixtureProject.Graph, budget: 0);
            foreach (var output in new[] { unlimited.Query("furniture request", depth: 4), unlimited.Query("place furniture request", limit: 2, depth: 4) })
            {
                var facts = Links(output).Select(Normalise).ToList();

                facts.Should().OnlyHaveUniqueItems();
                foreach (var (source, relation, target) in facts)
                    FixtureProject.Graph.Edges.Should().Contain(e => Label(e.Source) == source && e.Relation == relation && Label(e.Target) == target,
                        $"{source} {relation} {target} is an edge of the graph");
            }

            // The seed lists it; the system one step out, walked through, does not repeat it.
            var two = Links(unlimited.Query("place furniture request", limit: 2));
            two.Should().Contain(("PlaceFurnitureRequest", "created by", "RequestPlacementSystem"))
                .And.Contain(("RequestPlacementSystem", "creates", "OrphanRequest"))
                .And.NotContain(("RequestPlacementSystem", "creates", "PlaceFurnitureRequest"));
        }

        [Fact]
        public void Lines_carry_the_mechanism_inferred_marks_and_the_file()
        {
            var output = Queries.Query("furniture request");

            output.Should().Contain("    reads PlaceFurnitureRequest  via Matcher, Get  FurnitureSystems.cs:31");
            output.Should().Contain("    creates Furniture  via Flag* (FurnitureFactory.CreateFurniture)  Infrastructure.cs:32");
            output.Should().Contain("  RequestPlacementSystem  [system; FurnitureFeature #1, GameplayFeature order 2]  Systems/FurnitureSystems.cs:6",
                "a system's header carries the full path its lines point into");
        }

        [Fact]
        public void Every_node_hangs_off_a_line_above_it()
        {
            foreach (var queries in new[] { new GraphQueries(FixtureProject.Graph, budget: 0), new GraphQueries(FixtureProject.Graph, budget: 150) })
            {
                var output = queries.Query("furniture request", depth: 3);
                var named = new HashSet<string>(Sections(output)["Seeds:"]);
                foreach (var line in Body(output))
                {
                    if (IsNode(line))
                        named.Should().Contain(Header(line), "a node is listed only after a line that leads to it");
                    else if (IsLink(line))
                        named.Add(Other(line));
                }
            }
        }

        [Fact]
        public void An_answer_over_the_budget_follows_fewer_links_and_summarises_the_subgraph()
        {
            var full = Queries.Query("furniture request");
            var capped = new GraphQueries(FixtureProject.Graph, budget: 60).Query("furniture request");

            full.Should().NotContain("not shown").And.NotContain("more link", "the default budget fits the fixture's whole subgraph");
            capped.Should().StartWith("query \"furniture request\": 10 nodes match; seeds: ");
            capped.Should().Contain("\n  seeds 5; subgraph: ").And.Contain(" edges, depth 2, at most 6 links per node\n");
            capped.Should().Contain("\n  hubs not expanded: none\n");
            capped.Should().Contain("    … 1 more link: creates 1 — explain PlaceFurnitureByRequestSystem", "it has seven links");
            capped.Should().Contain("more nodes not shown — the answer is capped at ~60 tokens (--budget 0 prints everything).");
            new GraphQueries(FixtureProject.Graph, budget: 60, hubThreshold: 3).Query("furniture request")
                .Should().Contain("\n  hubs not expanded: Destructed, SelectSystem, ShopItem, Unpaid, Selected\n");
        }

        [Fact]
        public void Words_that_match_nothing_are_not_found_with_what_they_come_close_to()
        {
            Action typo = () => Queries.Query("furnitre");
            Action nothing = () => Queries.Query("zzzz qqqq");
            Action tooShort = () => Queries.Query("a");

            typo.Should().Throw<NodeNotFoundException>().Which.Suggestions.Select(s => s.Label).Should().Contain("Furniture");
            nothing.Should().Throw<NodeNotFoundException>().WithMessage("no node matches \"zzzz qqqq\"").Which.Suggestions.Should().BeEmpty();
            tooShort.Should().Throw<NodeNotFoundException>().WithMessage("nothing to search for in \"a\"*");
        }

        // -- a graph the fixture cannot give: namespaces ---------------------------------------------

        /// <summary>
        /// What made `query furniture placement` seed five peripheral systems on a 2000-file
        /// project, in miniature. Every system of the placement feature has "placement" in its
        /// namespace and "furniture" in its name. The system that places furniture lives in the
        /// furniture feature, says "placement" only as Place, and creates 45 components through a
        /// factory — inferred edges that made it a hub and won it every tie on degree.
        /// </summary>
        static EcsGraph PlacementFeature()
        {
            var graph = new EcsGraph();

            string Node(string kind, string fullName, string? role = null)
            {
                graph.Nodes.Add(new GraphNode { Id = fullName, Label = fullName.Substring(fullName.LastIndexOf('.') + 1), Kind = kind, FullName = fullName, Role = role });
                return fullName;
            }

            void Edge(string source, string relation, string target, string? through = null) => graph.Edges.Add(new GraphEdge
            {
                Source = source, Target = target, Relation = relation, Via = relation == Relation.Reads ? "AllOf" : "Add",
                Confidence = through == null ? Confidence.Extracted : Confidence.Inferred, Through = through, SourceFile = "Code.cs", Line = graph.Edges.Count + 1
            });

            var request = Node(NodeKind.Component, "Game.Furniture.PlaceFurnitureRequest", "request");
            var placed = Node(NodeKind.Component, "Game.Furniture.FurniturePlacedEvent", "event");
            var snapshot = Node(NodeKind.Component, "Game.Furniture.ReplacementSnapshot");
            var position = Node(NodeKind.Component, "Game.Common.Position");
            var guid = Node(NodeKind.Component, "Game.Common.Guid");
            var ghost = Node(NodeKind.Component, "Game.Placement.Ghost");
            var hue = Node(NodeKind.Component, "Game.Placement.Hue");
            var replacing = Node(NodeKind.Component, "Game.Placement.StartReplacingFurnitureRequest", "request");

            var place = Node(NodeKind.System, "Game.Furniture.PlaceFurnitureByRequestSystem");
            Edge(place, Relation.Reads, request);
            Edge(place, Relation.Destroys, request);
            Edge(place, Relation.Reads, position);
            Edge(place, Relation.Creates, placed);
            var factory = Node(NodeKind.Type, "Game.Furniture.FurnitureFactory");
            for (var i = 1; i <= 45; i++)
            {
                var part = Node(NodeKind.Component, $"Game.Parts.Part{i:00}");
                Edge(factory, Relation.Creates, part);
                Edge(place, Relation.Creates, part, "FurnitureFactory.Create");
            }

            Edge(Node(NodeKind.System, "Game.Input.HandleClickSystem"), Relation.Creates, request);
            Edge(Node(NodeKind.System, "Game.Audio.PlaySoundOnPlacedSystem"), Relation.Reads, placed);

            var end = Node(NodeKind.System, "Game.Placement.EndFurniturePlacementOnBuildModeStopSystem");
            foreach (var component in new[] { ghost, hue, position, guid })
                Edge(end, Relation.Reads, component);
            Edge(end, Relation.Removes, ghost);
            Edge(end, Relation.Creates, position);

            var start = Node(NodeKind.System, "Game.Placement.StartReplacingFurnitureByRequestSystem");
            foreach (var component in new[] { replacing, ghost, hue, guid })
                Edge(start, Relation.Reads, component);
            Edge(start, Relation.Destroys, replacing);
            Edge(start, Relation.Creates, snapshot);

            var move = Node(NodeKind.System, "Game.Placement.MoveFurnitureGhostSystem");
            Edge(move, Relation.Reads, ghost);
            Edge(move, Relation.Creates, position);

            Edge(Node(NodeKind.System, "Game.Placement.StartReplacingByClickSystem"), Relation.Creates, replacing);
            return graph;
        }

        // -- reading an answer -----------------------------------------------------------------

        /// <summary>The lines from "Seeds:" on: the head and a cut answer's summary are not nodes.</summary>
        static IEnumerable<string> Body(string output) => output.Split('\n').SkipWhile(l => l != "Seeds:");

        static bool IsNode(string line) => line.StartsWith("  ", StringComparison.Ordinal) && line.Length > 2 && line[2] != ' ';

        static bool IsLink(string line) => line.StartsWith("    ", StringComparison.Ordinal) && !line.TrimStart().StartsWith("…", StringComparison.Ordinal);

        static string Header(string line) => line.Trim().Split(' ')[0];

        /// <summary>"    created by RequestPlacementSystem  via Add  …" → the verb and the other node.</summary>
        static (string Verb, string Other) Link(string line)
        {
            var left = line.Trim().Split(new[] { "  via " }, StringSplitOptions.None)[0];
            var space = left.LastIndexOf(' ');
            return (left.Substring(0, space), left.Substring(space + 1));
        }

        static string Other(string line) => Link(line).Other;

        /// <summary>Node labels per section ("Seeds:", "1 step out:", …).</summary>
        static Dictionary<string, List<string>> Sections(string output)
        {
            var sections = new Dictionary<string, List<string>>();
            List<string>? current = null;
            foreach (var line in output.Split('\n'))
            {
                if (line == "Seeds:" || line.EndsWith(" out:", StringComparison.Ordinal))
                    sections[line] = current = new List<string>();
                else if (current != null && IsNode(line))
                    current.Add(Header(line));
            }
            return sections;
        }

        /// <summary>Every link line as (the node it is listed under, verb, other node).</summary>
        static List<(string Node, string Verb, string Other)> Links(string output)
        {
            var links = new List<(string, string, string)>();
            string? node = null;
            foreach (var line in Body(output))
            {
                if (IsNode(line)) node = Header(line);
                else if (node != null && IsLink(line))
                {
                    var (verb, other) = Link(line);
                    links.Add((node, verb, other));
                }
            }
            return links;
        }

        static readonly Dictionary<string, string> Active = new[]
            { Relation.Creates, Relation.Reads, Relation.Filters, Relation.Removes, Relation.Destroys, Relation.Reacts }
            .ToDictionary(GraphQueries.Passive, r => r);

        static (string Source, string Relation, string Target) Normalise((string Node, string Verb, string Other) link) =>
            Active.TryGetValue(link.Verb, out var relation) ? (link.Other, relation, link.Node) : (link.Node, link.Verb, link.Other);

        static string Label(string id) => FixtureProject.Graph.Node(id)!.Label;
    }
}
