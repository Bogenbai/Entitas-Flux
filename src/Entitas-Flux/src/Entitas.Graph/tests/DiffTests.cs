using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using Entitas.Graph.Output;
using Entitas.SourceGenerator.Cli;
using FluentAssertions;
using Xunit;

namespace Entitas.Graph.Tests
{
    /// <summary>
    /// entitas-graph diff: the Fixture/ graph against a copy of itself (round-tripped through
    /// graph.json, as the command reads it) with one change at a time.
    /// </summary>
    public class DiffTests
    {
        static EcsGraph Copy() => EcsGraph.FromJson(FixtureProject.Graph.ToJson());

        static GraphNode Node(EcsGraph graph, string label, string kind) => graph.Nodes.Single(n => n.Label == label && n.Kind == kind);

        static string Diff(EcsGraph changed, int budget = GraphQueries.DefaultBudget)
        {
            changed.Sort();
            return GraphDiff.Compare(Copy(), changed).Render("old.json", "new.json", budget);
        }

        static string[] Lines(string output) => output.Split('\n').Select(l => l.TrimEnd()).ToArray();

        [Fact]
        public void Identical_graphs_have_no_changes()
        {
            var old = Copy();
            old.Metadata.BuiltAtCommit = "0123456789abcdef0123456789abcdef01234567";

            var diff = GraphDiff.Compare(old, Copy());
            var output = diff.Render("main", "working tree");

            diff.IsEmpty.Should().BeTrue();
            Lines(output).Should().StartWith(new[] { "old: main (commit 0123456789)", "new: working tree" });
            output.Should().Contain("No changes in the data flow");
        }

        [Fact]
        public void Lines_files_counts_and_vias_are_not_changes()
        {
            var moved = Copy();
            foreach (var edge in moved.Edges)
            {
                edge.Line += 7;
                edge.Count += 1;
                edge.SourceFile = "Assets/Elsewhere.cs";
            }
            moved.Edges.First(e => e.Relation == Relation.Reads && e.Via == "AllOf").Via = "Access";

            GraphDiff.Compare(Copy(), moved).IsEmpty.Should().BeTrue("an edge is (source, target, relation); the rest moves with every edit");
        }

        [Fact]
        public void A_lost_reader_is_listed_under_its_component_with_the_findings_it_causes()
        {
            var changed = Copy();
            var unlock = Node(changed, "UnlockAchievementSystem", NodeKind.System);
            var placed = Node(changed, "FurniturePlacedEvent", NodeKind.Component);
            changed.Edges.RemoveAll(e => e.Source == unlock.Id && e.Target == placed.Id && e.Relation == Relation.Reads);

            var output = Diff(changed);

            Lines(output).Should().Contain(new[]
            {
                "old: old.json",
                "new: new.json",
                "nodes: none added or removed",
                "edges: reads −1",
                "  FurniturePlacedEvent  −1",
                "    − read by UnlockAchievementSystem (AllOf)",
                "  + Events without a consumer (`*Event`): FurniturePlacedEvent",
                "  + Components nobody reads: FurniturePlacedEvent"
            });
        }

        [Fact]
        public void A_new_component_and_its_creator_are_listed()
        {
            var changed = Copy();
            var request = Node(changed, "RequestPlacementSystem", NodeKind.System);
            changed.Nodes.Add(new GraphNode
            {
                Id = "component:ShopPurchaseEvent", Label = "ShopPurchaseEvent", Kind = NodeKind.Component, Role = "event",
                Contexts = { "Game" }, Fields = { "Value" }
            });
            changed.Edges.Add(new GraphEdge { Source = request.Id, Target = "component:ShopPurchaseEvent", Relation = Relation.Creates, Via = "Add", Entity = "new" });

            var output = Diff(changed);

            Lines(output).Should().Contain(new[]
            {
                "nodes: components +1",
                "edges: creates +1",
                "  ShopPurchaseEvent  +1  (new component)",
                "    + created by RequestPlacementSystem (Add)",
                "  + Events without a consumer (`*Event`): ShopPurchaseEvent"
            });
        }

        [Fact]
        public void Inferred_edges_are_marked_with_what_they_went_through()
        {
            var changed = Copy();
            var place = Node(changed, "PlaceFurnitureByRequestSystem", NodeKind.System);
            changed.Edges.RemoveAll(e => e.Source == place.Id && e.Target == "component:Furniture" && e.Relation == Relation.Creates);

            Lines(Diff(changed)).Should().Contain("    − created by PlaceFurnitureByRequestSystem (Flag*, through FurnitureFactory.CreateFurniture)");
        }

        [Fact]
        public void A_system_moved_among_its_siblings_is_reported_and_the_renumbered_one_only_counted()
        {
            var changed = Copy();
            var feature = Node(changed, "FurnitureFeature", NodeKind.Feature);
            Register(changed, feature, "CountFurnitureSystem", 4);
            Register(changed, feature, "SelectSystem", 3);

            var output = Diff(changed);

            Lines(output).Should().Contain(new[]
            {
                "  ~ CountFurnitureSystem: FurnitureFeature #3 → #4",
                "  1 more renumbered, their order among the others unchanged: FurnitureFeature 1"
            });
            output.Should().NotContain("~ SelectSystem").And.Contain("1 registrations moved (1 renumbered)");
        }

        [Fact]
        public void A_removed_system_is_listed_with_where_it_was_and_every_edge_it_took_along()
        {
            var changed = Copy();
            var feature = Node(changed, "FurnitureFeature", NodeKind.Feature);
            var damage = Node(changed, "DamageSystem", NodeKind.System);
            changed.Nodes.Remove(damage);
            changed.Edges.RemoveAll(e => e.Source == damage.Id || e.Target == damage.Id);
            Register(changed, feature, "UnusedSystem", 7);

            var output = Diff(changed);

            Lines(output).Should().Contain(new[]
            {
                "nodes: systems −1",
                "  − DamageSystem  (was FurnitureFeature #7)",
                "  Health  −3",
                "    − read by DamageSystem (AllOf, Get) — system removed",
                "  1 more renumbered, their order among the others unchanged: FurnitureFeature 1",
                "  − Components read but never created: Dead"
            });
            output.Should().Contain("contains −1");
        }

        [Fact]
        public void Components_with_the_most_changes_come_first()
        {
            var changed = Copy();
            var damage = Node(changed, "DamageSystem", NodeKind.System);
            changed.Edges.RemoveAll(e => e.Source == damage.Id && e.Target == "component:Health");
            var unlock = Node(changed, "UnlockAchievementSystem", NodeKind.System);
            changed.Edges.RemoveAll(e => e.Source == unlock.Id && e.Target == "component:FurniturePlacedEvent");

            var output = Diff(changed);

            output.IndexOf("  Health  −3", StringComparison.Ordinal).Should().BeGreaterThan(0)
                .And.BeLessThan(output.IndexOf("  FurniturePlacedEvent  −1", StringComparison.Ordinal));
        }

        [Fact]
        public void A_cut_diff_keeps_the_counts_and_names_everything_changed()
        {
            var changed = Copy();
            var damage = Node(changed, "DamageSystem", NodeKind.System);
            changed.Nodes.Remove(damage);
            changed.Edges.RemoveAll(e => e.Source == damage.Id || e.Target == damage.Id);
            var unlock = Node(changed, "UnlockAchievementSystem", NodeKind.System);
            changed.Edges.RemoveAll(e => e.Source == unlock.Id && e.Target == "component:FurniturePlacedEvent");

            var full = Diff(changed, budget: 0);
            var cut = Diff(changed, budget: 40);

            Lines(cut).Should().StartWith(new[] { "old: old.json", "new: new.json", "nodes: systems −1" });
            cut.Should().Contain("  components, most changes first: Health −3, ")
                .And.Contain("  systems: removed DamageSystem")
                .And.Contain("  findings: +FurniturePlacedEvent")
                .And.Contain("more changes not shown — the answer is capped at ~40 tokens");
            cut.Length.Should().BeLessThan(full.Length);
            full.Should().NotContain("not shown");
        }

        static void Register(EcsGraph graph, GraphNode feature, string label, int index)
        {
            var node = Node(graph, label, NodeKind.System);
            graph.Edges.Single(e => e.Source == feature.Id && e.Target == node.Id && e.Relation == Relation.Contains).Order = index;
            node.RegisteredIn.Single(r => r.Feature == feature.Id).Index = index;
        }

        // -- building the old side from a git ref ---------------------------------------------

        [Fact]
        public void The_csproj_is_mapped_to_the_files_the_ref_has()
        {
            var listed = new[] { "Assets/Code/A.cs", "Assets/Code/Systems/B.cs", "Assets/Code/Systems/New.cs" };
            var atRef = new[]
            {
                "Assets/Code/A.cs",
                "Assets/Code/Systems/B.cs",
                "Assets/Code/Systems/Deleted.cs",      // next to listed files: deleted since
                "Assets/Code/Removed/Feature/C.cs",    // in a folder deleted since, below a listed one
                "Assets/Code/Editor/Tool.cs",          // Unity's editor assembly
                "Assets/Code/Package/Runtime/D.cs",    // below another asmdef
                "Assets/Plugins/E.cs"                  // no listed folder above it
            };

            var mapped = RefSnapshot.MapSources(listed, atRef, new[] { "Assets/Code/Package" });

            mapped.Should().Equal(
                "Assets/Code/A.cs",
                "Assets/Code/Removed/Feature/C.cs",
                "Assets/Code/Systems/B.cs",
                "Assets/Code/Systems/Deleted.cs");
        }

        [Fact]
        public void A_snapshot_holds_the_sources_of_the_ref_and_is_deleted_afterwards()
        {
            var repository = Path.Combine(Path.GetTempPath(), "entitas-graph-tests-" + Path.GetRandomFileName());
            var unity = Path.Combine(repository, "game");
            try
            {
                Write(unity, "Assets/Code/A.cs", "class A { }");
                Write(unity, "Assets/Code/Old.cs", "class Old { }");
                Git(repository, "init", "-q");
                Git(repository, "add", ".");
                Git(repository, "-c", "user.name=test", "-c", "user.email=test@example.com", "-c", "commit.gpgsign=false", "commit", "-q", "-m", "old");

                Write(unity, "Assets/Code/A.cs", "class A { int _changed; }");
                Write(unity, "Assets/Code/New.cs", "class New { }");
                File.Delete(Path.Combine(unity, "Assets/Code/Old.cs"));
                var current = new ProjectLoader.ProjectInfo(
                    new[] { "Assets/Code/A.cs", "Assets/Code/New.cs" }.Select(p => Path.Combine(unity, p)).ToList(),
                    Array.Empty<string>(), Array.Empty<string>(), new[] { "UNITY_EDITOR" });

                string root;
                using (var snapshot = RefSnapshot.Extract(unity, current, "HEAD"))
                {
                    root = snapshot.Root;
                    snapshot.Commit.Should().HaveLength(40);
                    snapshot.Project.SourceFiles.Select(f => Path.GetRelativePath(root, f).Replace('\\', '/'))
                        .Should().Equal("Assets/Code/A.cs", "Assets/Code/Old.cs");
                    File.ReadAllText(Path.Combine(root, "Assets/Code/A.cs")).Should().Be("class A { }");
                    snapshot.Project.Defines.Should().Equal("UNITY_EDITOR");
                    (snapshot.AddedFiles, snapshot.DroppedFiles).Should().Be((1, 1));
                }

                Directory.Exists(root).Should().BeFalse();
                File.ReadAllText(Path.Combine(unity, "Assets/Code/A.cs")).Should().Be("class A { int _changed; }", "the checkout is never touched");
            }
            finally
            {
                if (Directory.Exists(repository))
                    Directory.Delete(repository, recursive: true);
            }
        }

        static void Write(string root, string relative, string text)
        {
            var path = Path.Combine(root, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, text);
        }

        static void Git(string directory, params string[] arguments)
        {
            var info = new ProcessStartInfo("git") { WorkingDirectory = directory, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
            foreach (var argument in arguments)
                info.ArgumentList.Add(argument);
            using var process = Process.Start(info)!;
            var error = process.StandardError.ReadToEnd();
            process.WaitForExit();
            process.ExitCode.Should().Be(0, error);
        }
    }
}
