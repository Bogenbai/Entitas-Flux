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

        [Fact]
        public void The_header_names_both_sides_and_says_how_they_were_chosen()
        {
            var old = Copy();
            old.Metadata.BuiltAtCommit = "3afbaa9cc5000000000000000000000000000000";
            var @new = Copy();
            @new.Metadata.BuiltAtCommit = "49904c1de3000000000000000000000000000000";
            var request = DiffRequest.From(Array.Empty<string>(), "49904c1d", Array.Empty<string>())
                .WithParents(new[] { "3afbaa9cc5000000000000000000000000000000", "7777777777000000000000000000000000000000" });

            var output = GraphDiff.Compare(old, @new).Render(request.Old.Label, request.New.Label, note: request.Note);

            Lines(output).Should().StartWith(new[]
            {
                "old: 49904c1d^ (commit 3afbaa9cc5)",
                "new: 49904c1d (commit 49904c1de3)",
                "49904c1d is a merge: old is its first parent, so this is what the merge brought into that branch."
            });
        }

        // -- what diff compares -----------------------------------------------------------------

        static readonly string[] None = Array.Empty<string>();

        [Fact]
        public void Two_refs_compare_the_first_with_the_second()
        {
            var request = DiffRequest.From(new[] { "main", "feature" }, null, None);

            (request.Old.Source, request.Old.Value, request.Old.Label).Should().Be((DiffSource.GitRef, "main", "main"));
            (request.New.Source, request.New.Value, request.New.Label).Should().Be((DiffSource.GitRef, "feature", "feature"));
            request.Note.Should().BeNull();
        }

        [Fact]
        public void One_ref_or_one_graph_file_compares_with_the_working_tree()
        {
            DiffRequest.From(new[] { "main" }, null, None).New.Should().BeSameAs(DiffSide.WorkingTree);
            DiffRequest.From(None, null, new[] { "old.json" }).New.Should().BeSameAs(DiffSide.WorkingTree);

            var files = DiffRequest.From(None, null, new[] { "old.json", "new.json" });
            (files.Old.Source, files.Old.Value, files.New.Value).Should().Be((DiffSource.GraphFile, "old.json", "new.json"));
        }

        [Fact]
        public void A_commit_is_compared_with_its_first_parent_by_id()
        {
            var request = DiffRequest.From(None, "49904c1d", None);
            (request.Old.Label, request.New.Label, request.New.Value).Should().Be(("49904c1d^", "49904c1d", "49904c1d"));

            var resolved = request.WithParents(new[] { "3afbaa9cc5" });

            (resolved.Old.Source, resolved.Old.Value, resolved.Old.Label).Should().Be((DiffSource.GitRef, "3afbaa9cc5", "49904c1d^"));
            resolved.New.Should().BeSameAs(request.New);
            resolved.Note.Should().BeNull("an ordinary commit has one parent; nothing to explain");
        }

        [Fact]
        public void A_merge_is_compared_with_its_first_parent_and_says_so()
        {
            var resolved = DiffRequest.From(None, "0e8a4184", None).WithParents(new[] { "f3c6f93b53", "a1b2c3d4e5" });

            resolved.Old.Value.Should().Be("f3c6f93b53");
            resolved.Note.Should().Contain("0e8a4184 is a merge").And.Contain("first parent");
        }

        [Fact]
        public void A_root_commit_has_nothing_to_compare_with()
        {
            var request = DiffRequest.From(None, "a1b2c3d4", None);

            request.Invoking(r => r.WithParents(None)).Should().Throw<InvalidOperationException>()
                .WithMessage("a1b2c3d4 is a root commit*diff --ref a1b2c3d4*");
        }

        [Theory]
        [InlineData("a,b,c", null, "", "at most 2 --ref")]
        [InlineData("a", "c", "", "--commit c already names both sides")]
        [InlineData("a,b", "c", "", "--commit c already names both sides")]
        [InlineData("", "c", "old.json", "--commit <commit> or graph files")]
        [InlineData("a", null, "old.json", "--ref <git-ref> or graph files")]
        [InlineData("", null, "", "needs a graph file, --ref")]
        [InlineData("", null, "a.json,b.json,c.json", "at most two graph files")]
        public void Combinations_that_name_no_two_sides_are_usage_errors(string refs, string? commit, string files, string message)
        {
            static string[] List(string csv) => csv.Length == 0 ? None : csv.Split(',');

            FluentActions.Invoking(() => DiffRequest.From(List(refs), commit, List(files)))
                .Should().Throw<DiffUsageException>().WithMessage($"*{message}*");
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

    /// <summary>
    /// The diff command end to end, through the program's entry point: option parsing, and two
    /// refs or a commit built from a tiny git repository with a minimal Entitas project. Not run
    /// in parallel with anything — the program writes to Console, which is redirected here.
    /// </summary>
    [Collection(nameof(DiffCommandTests))]
    public class DiffCommandTests : IClassFixture<DiffCommandTests.History>
    {
        readonly History _history;

        public DiffCommandTests(History history) => _history = history;

        [Theory]
        [InlineData("at most 2 --ref", "diff", "--ref", "a", "--ref", "b", "--ref", "c")]
        [InlineData("use it or --ref, not both", "diff", "--commit", "a", "--ref", "b")]
        [InlineData("use it or --ref, not both", "diff", "--ref", "b", "--commit", "a")]
        [InlineData("--commit takes one commit", "diff", "--commit", "a", "--commit", "b")]
        public void Conflicting_diff_options_are_usage_errors_before_anything_is_built(string message, params string[] args)
        {
            var (exit, output, error) = Run(args);

            exit.Should().Be(2);
            output.Should().BeEmpty();
            error.Should().Contain("error: ").And.Contain(message).And.Contain("Usage: entitas-graph");
        }

        [Fact]
        public void A_commit_is_diffed_against_its_parent_whatever_came_after_it()
        {
            var commit = _history.SwitchToBeta.Substring(0, 8);

            var (exit, output, error) = Run("diff", "--commit", commit, "-r", _history.Unity, "-o", _history.Out, "--budget", "0");

            exit.Should().Be(0, error);
            var lines = output.Split('\n').Select(l => l.TrimEnd()).ToArray();
            lines.Should().StartWith(new[]
            {
                $"old: {commit}^ (commit {_history.Root.Substring(0, 10)})",
                $"new: {commit} (commit {_history.SwitchToBeta.Substring(0, 10)})"
            });
            lines.Should().Contain(new[] { "  Alpha  −1", "    − read by ReadSystem (Matcher)", "  Beta  +1", "    + read by ReadSystem (Matcher)" });
            output.Should().NotContain("OtherSystem", "it came in a later commit").And.NotContain("LaterSystem").And.NotContain("SideSystem");
            error.Should().Contain("Both graphs built in parallel in ");
        }

        [Fact]
        public void Two_refs_give_what_the_commit_gives_and_one_ref_also_shows_what_came_after()
        {
            var commit = _history.SwitchToBeta.Substring(0, 8);
            var options = new[] { "-r", _history.Unity, "-o", _history.Out, "--budget", "0" };

            var byCommit = Run(new[] { "diff", "--commit", commit }.Concat(options).ToArray());
            var byRefs = Run(new[] { "diff", "--ref", commit + "^", "--ref", commit }.Concat(options).ToArray());
            var againstWorkingTree = Run(new[] { "diff", "--ref", commit + "^" }.Concat(options).ToArray());

            byRefs.Exit.Should().Be(0, byRefs.Error);
            byRefs.Output.Should().Be(byCommit.Output);
            againstWorkingTree.Output.Should().Contain("new: working tree").And.Contain("read by OtherSystem");
        }

        [Fact]
        public void A_merge_is_diffed_against_its_first_parent_and_says_so()
        {
            var merge = _history.Merge.Substring(0, 8);

            var (exit, output, error) = Run("diff", "--commit", merge, "-r", _history.Unity, "-o", _history.Out, "--budget", "0");

            exit.Should().Be(0, error);
            output.Should().Contain($"{merge} is a merge: old is its first parent")
                .And.Contain("+ read by SideSystem (Matcher) — new system", "the merged branch is what the merge brought in")
                .And.NotContain("LaterSystem", "the first parent already had it");
        }

        [Fact]
        public void A_root_commit_is_a_clear_error()
        {
            var (exit, output, error) = Run("diff", "--commit", _history.Root, "-r", _history.Unity, "-o", _history.Out);

            exit.Should().Be(1);
            output.Should().BeEmpty();
            error.Should().Contain($"{_history.Root} is a root commit: it has no parent to compare it with");
        }

        static (int Exit, string Output, string Error) Run(params string[] args)
        {
            var output = new StringWriter();
            var error = new StringWriter();
            var (stdout, stderr) = (Console.Out, Console.Error);
            Console.SetOut(output);
            Console.SetError(error);
            try
            {
                var exit = (int)typeof(EcsGraph).Assembly.EntryPoint!.Invoke(null, new object[] { args })!;
                return (exit, output.ToString(), error.ToString());
            }
            finally
            {
                Console.SetOut(stdout);
                Console.SetError(stderr);
            }
        }

        /// <summary>
        /// A repository whose Unity project sits in game/, with a history in which the commit
        /// under test is followed by more changes to the same components, then a merge:
        ///
        ///   Root           Alpha, Beta; ReadSystem reads Alpha
        ///   SwitchToBeta   ReadSystem reads Beta instead
        ///   (next)         OtherSystem reads Alpha
        ///   (main)         LaterSystem reads Alpha       (side) SideSystem reads Beta
        ///   Merge          side merged into main
        ///
        /// The csproj is written last and not committed, like Unity's: it lists today's files.
        /// </summary>
        public sealed class History : IDisposable
        {
            readonly string _repository = Path.Combine(Path.GetTempPath(), "entitas-graph-tests-" + Path.GetRandomFileName());

            public string Unity => Path.Combine(_repository, "game");
            public string Out => Path.Combine(_repository, "out");
            public string Root { get; }
            public string SwitchToBeta { get; }
            public string Merge { get; }

            public History()
            {
                Write("Assets/Code/Game.cs", @"using Entitas;
using Entitas.CodeGeneration.Attributes;

[assembly: ContextDefinition(""Game"")]

[Game] public sealed class Alpha : IComponent { public int Value; }
[Game] public sealed class Beta : IComponent { public int Value; }
");
                Write("Assets/Code/ReadSystem.cs", Reader("ReadSystem", "Alpha"));
                Git("init", "-q");
                Root = Commit("root");

                Write("Assets/Code/ReadSystem.cs", Reader("ReadSystem", "Beta"));
                SwitchToBeta = Commit("read Beta");

                Write("Assets/Code/OtherSystem.cs", Reader("OtherSystem", "Alpha"));
                Commit("other");
                Git("branch", "side");

                Write("Assets/Code/LaterSystem.cs", Reader("LaterSystem", "Alpha"));
                Commit("later");
                Git("checkout", "-q", "side");
                Write("Assets/Code/SideSystem.cs", Reader("SideSystem", "Beta"));
                Commit("side");
                Git("checkout", "-q", "-");
                Git("-c", "user.name=test", "-c", "user.email=test@example.com", "-c", "commit.gpgsign=false", "merge", "-q", "--no-ff", "-m", "merge", "side");
                Merge = Git("rev-parse", "HEAD");

                var sources = Directory.EnumerateFiles(Unity, "*.cs", SearchOption.AllDirectories).OrderBy(p => p, StringComparer.Ordinal);
                var runtime = ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!).Split(Path.PathSeparator)
                    .Where(path => Path.GetFileName(path) is "netstandard.dll" or "mscorlib.dll" || Path.GetFileName(path).StartsWith("System.", StringComparison.Ordinal));
                var references = runtime
                    .Append(typeof(Entitas.IComponent).Assembly.Location)
                    .Append(typeof(Entitas.CodeGeneration.Attributes.ContextAttribute).Assembly.Location);
                Write("Assembly-CSharp.csproj",
                    "<Project ToolsVersion=\"4.0\" xmlns=\"http://schemas.microsoft.com/developer/msbuild/2003\">\n" +
                    "  <ItemGroup>\n" + string.Concat(sources.Select(path => $"    <Compile Include=\"{Path.GetRelativePath(Unity, path)}\" />\n")) + "  </ItemGroup>\n" +
                    "  <ItemGroup>\n" + string.Concat(references.Select(path => $"    <Reference Include=\"{Path.GetFileNameWithoutExtension(path)}\"><HintPath>{path}</HintPath></Reference>\n")) + "  </ItemGroup>\n" +
                    "</Project>\n");
            }

            // In the global namespace, so the component's generated name is its class name.
            static string Reader(string system, string component) => $@"using Entitas;

public sealed class {system} : IExecuteSystem
{{
    readonly IGroup<GameEntity> _group;

    public {system}(GameContext game) => _group = game.GetGroup(GameMatcher.{component});

    public void Execute()
    {{
        foreach (var entity in _group) {{ }}
    }}
}}
";

            void Write(string relative, string text)
            {
                var path = Path.Combine(Unity, relative);
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                File.WriteAllText(path, text);
            }

            string Commit(string message)
            {
                Git("add", ".");
                Git("-c", "user.name=test", "-c", "user.email=test@example.com", "-c", "commit.gpgsign=false", "commit", "-q", "-m", message);
                return Git("rev-parse", "HEAD");
            }

            string Git(params string[] arguments)
            {
                var info = new ProcessStartInfo("git") { WorkingDirectory = _repository, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
                foreach (var argument in arguments)
                    info.ArgumentList.Add(argument);
                using var process = Process.Start(info)!;
                var error = process.StandardError.ReadToEndAsync();
                var output = process.StandardOutput.ReadToEnd();
                process.WaitForExit();
                process.ExitCode.Should().Be(0, error.Result);
                return output.Trim();
            }

            public void Dispose()
            {
                if (Directory.Exists(_repository))
                    Directory.Delete(_repository, recursive: true);
            }
        }
    }

    /// <summary>The command tests redirect Console, so nothing may run beside them.</summary>
    [CollectionDefinition(nameof(DiffCommandTests), DisableParallelization = true)]
    public class DiffCommandTestsCollection
    {
    }
}
