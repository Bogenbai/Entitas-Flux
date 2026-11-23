using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using Entitas.Graph.Analysis;
using Entitas.Graph.Mcp;
using Entitas.Graph.Output;
using Entitas.Graph.Setup;
using Entitas.SourceGenerator.Cli;

namespace Entitas.Graph
{
    /// <summary>
    /// entitas-graph: who creates, reads, filters, removes and destroys each component, which
    /// features run which systems in what order — built from Roslyn semantics, so a chained
    /// CreateEntity.Event().AddX() or a GameMatcher.X inside AllOf counts like any other use.
    /// </summary>
    internal static class Program
    {
        const int Ok = 0, Failed = 1, Usage = 2, Ambiguous = 3, NotFound = 4;

        static int Main(string[] args)
        {
            // The Claude Code hook (through the sh wrapper): first, so it pays for nothing else.
            if (args.Length == 1 && args[0] == ClaudeHook.Flag)
                return ClaudeHookCheck.Run(Console.In, Console.Error);

            try
            {
                var options = Options.Parse(args);
                if (options == null)
                {
                    PrintUsage();
                    return Usage;
                }

                if (options.Command is "install" or "uninstall")
                    return InstallOrUninstall(options);

                if (options.Command == "build")
                {
                    Build(options);
                    return Ok;
                }

                if (options.Command == "mcp")
                {
                    if (options.Positional.Count > 0) throw new UsageException("'mcp' takes no arguments");
                    Serve(options);
                    return Ok;
                }

                if (options.Command == "diff")
                {
                    Console.Write(Diff(options));
                    return Ok;
                }

                var graph = LoadOrBuild(options);
                var queries = new GraphQueries(graph, options.Budget);
                var output = options.Command switch
                {
                    "who-creates" => queries.WhoCreates(options.Arg(0)),
                    "who-reads" => queries.WhoReads(options.Arg(0)),
                    "lifecycle" => queries.Lifecycle(options.Arg(0)),
                    "systems-of" => queries.SystemsOf(options.Arg(0)),
                    "explain" => queries.Explain(options.Arg(0)),
                    "path" => queries.Path(options.Arg(0), options.Arg(1)),
                    "query" => queries.Query(options.Words(), options.Limit, options.DepthFor(GraphQueries.DefaultQueryDepth, GraphQueries.MaxQueryDepth)),
                    "flow" => queries.Flow(options.Arg(0), options.DepthFor(GraphQueries.DefaultFlowDepth, GraphQueries.MaxFlowDepth)),
                    "report" => GraphReport.Build(graph),
                    _ => throw new UsageException($"unknown command '{options.Command}'")
                };
                Console.Write(output);
                return Ok;
            }
            catch (UsageException ex)
            {
                Console.Error.WriteLine($"error: {ex.Message}");
                PrintUsage();
                return Usage;
            }
            catch (SetupException ex)
            {
                Console.Error.WriteLine($"error: {ex.Message}");
                return Usage;
            }
            catch (AmbiguousNameException ex)
            {
                Console.Error.Write(NameErrors.Ambiguous(ex));
                return Ambiguous;
            }
            catch (NodeNotFoundException ex)
            {
                Console.Error.Write(NameErrors.NotFound(ex));
                return NotFound;
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"error: {ex.Message}");
                return Failed;
            }
        }

        static void PrintUsage()
        {
            Console.Error.WriteLine("Usage: entitas-graph <command> [args] [options]");
            Console.Error.WriteLine();
            Console.Error.WriteLine("  build                       Build the graph: graph.json, GRAPH_REPORT.md, graph.html.");
            Console.Error.WriteLine("  who-creates <Component>     Systems and types that create it (Add, Replace, isX = true, factories).");
            Console.Error.WriteLine("  who-reads <Component>       Systems and types that read it (groups, accessors, [Watched] reactions).");
            Console.Error.WriteLine("  lifecycle <Component>       Create → read → remove/destroy, per root feature in execution order.");
            Console.Error.WriteLine("  systems-of <Feature>        The systems a feature runs, in order, nested features expanded.");
            Console.Error.WriteLine("  explain <Name>              A node and all its connections.");
            Console.Error.WriteLine("  path <A> <B>                The shortest data-flow chain from A to B.");
            Console.Error.WriteLine("  query <words…>              The connected subgraph around the nodes the words match best.");
            Console.Error.WriteLine("  flow <Component|System>     What happens after it: the systems its requests, events and [Watched]");
            Console.Error.WriteLine("                              changes reach, in order, each labelled same frame / next frame.");
            Console.Error.WriteLine("  report                      Print GRAPH_REPORT.md (findings).");
            Console.Error.WriteLine("  diff <old> [<new>]          What changed in the data flow between two graph.json (new: the current project).");
            Console.Error.WriteLine("  diff --ref <old> [--ref <new>]");
            Console.Error.WriteLine("                              The same, the project at a branch or commit against the working tree,");
            Console.Error.WriteLine("                              or against the project at a second one.");
            Console.Error.WriteLine("  diff --commit <commit>      What one commit changed: its first parent against it.");
            Console.Error.WriteLine("  mcp                         The query commands as tools of an MCP server on stdio, for assistants and");
            Console.Error.WriteLine("                              subagents without a shell (install registers it in .mcp.json).");
            Console.Error.WriteLine("  install                     Tell the project's AI assistant to use these commands: a block in CLAUDE.md");
            Console.Error.WriteLine("                              (and AGENTS.md, if present) in the git root, the short commands entitas-graph and\n" +
                                    "                              entitas-graph.cmd next to it, a Claude Code hook in .claude/settings.json that keeps\n" +
                                    "                              assistants out of graph.json, the MCP server in .mcp.json, and entitas-graph-out/ in .gitignore.");
            Console.Error.WriteLine("  uninstall                   Remove that block and those commands again.");
            Console.Error.WriteLine();
            Console.Error.WriteLine("  -r, --root <dir>            Unity project root (default: $ENTITAS_GRAPH_ROOT, set by the wrappers, else the current directory).");
            Console.Error.WriteLine("  -p, --project <csproj>      The project declaring the contexts (default: found under the root).");
            Console.Error.WriteLine("  -o, --out <dir>             Output folder (default: <root>/entitas-graph-out).");
            Console.Error.WriteLine("      --request-suffix <s>    Name suffix of request components (default: Request).");
            Console.Error.WriteLine("      --event-suffix <s>      Name suffix of event components (default: Event).");
            Console.Error.WriteLine("                              Components no suffix names are recognised by structure.");
            Console.Error.WriteLine("      --budget <tokens>       Cap each answer at about this many tokens (default: 2000; 0 = no cap).");
            Console.Error.WriteLine("      --limit <n>             Seed nodes for query (default: 5).");
            Console.Error.WriteLine("      --depth <n>             Steps query walks from the seeds (1-4, default: 2);");
            Console.Error.WriteLine("                              system hops flow follows (1-8, default: 4).");
            Console.Error.WriteLine("      --no-html               Skip graph.html on build.");
            Console.Error.WriteLine("      --ref <git-ref>         For diff: build a side at this commit (git archive; the checkout is not touched).");
            Console.Error.WriteLine("                              Once: old, against the working tree. Twice: old, then new.");
            Console.Error.WriteLine("      --commit <commit>       For diff: the commit's first parent against the commit.");
            Console.Error.WriteLine("      --target <file>         Instruction file for install/uninstall instead of the default (repeatable).");
            Console.Error.WriteLine("      --no-gitignore          install: leave .gitignore alone.");
            Console.Error.WriteLine("      --no-claude-hook        install: leave .claude/settings.json alone (no hook keeping Claude out of graph.json).");
            Console.Error.WriteLine("      --no-mcp                install: leave .mcp.json alone (no MCP server for agents without a shell).");
            Console.Error.WriteLine();
            Console.Error.WriteLine("Queries reuse graph.json and rebuild it first when a source file is newer. An answer over");
            Console.Error.WriteLine("the budget starts with a summary by feature and says how much it left out.");
            Console.Error.WriteLine("Exit codes: 0 answer, 1 error, 2 usage, 3 ambiguous name, 4 not found.");
        }

        // -- install / uninstall ------------------------------------------------------------

        /// <summary>The dll that is running: install names it, so the block points at the copy the project has.</summary>
        static string ToolPath()
        {
            var tool = typeof(Program).Assembly.Location;
            return tool.Length > 0 ? tool : Path.Combine(AppContext.BaseDirectory, "entitas-graph.dll");
        }

        static int InstallOrUninstall(Options options)
        {
            if (options.Positional.Count > 0) throw new UsageException($"'{options.Command}' takes no arguments");

            var tool = ToolPath();

            var result = options.Command == "install"
                ? AssistantSetup.Install(options.Root, options.Targets, options.Gitignore, tool, options.ClaudeHook, options.Mcp)
                : AssistantSetup.Uninstall(options.Root, options.Targets);

            var cwd = Directory.GetCurrentDirectory();
            foreach (var file in result.Files)
                Console.WriteLine($"{file.Outcome.ToString().ToLowerInvariant(),-10} {Relative(file.Path, cwd)}{(file.Detail != null ? "  — " + file.Detail : string.Empty)}");
            foreach (var note in result.Notes)
                Console.Error.WriteLine($"note: {note}");
            return Ok;
        }

        // -- mcp ----------------------------------------------------------------------------

        /// <summary>
        /// The MCP server on stdin/stdout until the client closes it. The graph is kept between
        /// calls and loaded again only when the sources are newer than graph.json, or graph.json
        /// changed under it (a build from the terminal); a call that finds it stale rebuilds it,
        /// as a CLI query would. Notes go to stderr, which the client logs.
        /// </summary>
        static void Serve(Options options)
        {
            EcsGraph? graph = null;
            var loaded = DateTime.MinValue;
            EcsGraph Current()
            {
                var path = GraphPath(options);
                if (graph != null && File.Exists(path) && File.GetLastWriteTimeUtc(path) == loaded && !IsStale(path, options.Root))
                    return graph;
                graph = LoadOrBuild(options);
                loaded = File.Exists(path) ? File.GetLastWriteTimeUtc(path) : DateTime.MinValue;
                return graph;
            }

            string Diff(string? commit, string? from, string? to)
            {
                var refs = new List<string>();
                if (from != null) refs.Add(from);
                if (to != null) refs.Add(to);
                var request = new Options { Command = "diff", Root = options.Root, Project = options.Project, OutOverride = options.OutOverride,
                    RequestSuffix = options.RequestSuffix, EventSuffix = options.EventSuffix, Budget = options.Budget, Commit = commit };
                request.Refs.AddRange(refs);
                return Program.Diff(request);
            }

            new McpServer(Current, Diff, options.Budget, Version()).Run(Console.In, Console.Out);
        }

        // -- building -----------------------------------------------------------------------

        static EcsGraph Build(Options options)
        {
            var stopwatch = Stopwatch.StartNew();
            var csproj = FindProject(options);
            var project = ProjectLoader.ParseProject(csproj);
            if (project.MissingSourceFiles.Count > 0)
                Console.Error.WriteLine($"note: {Path.GetFileName(csproj)} lists {project.MissingSourceFiles.Count} file(s) that no longer exist — let Unity regenerate it if something seems missing.");

            var graph = BuildGraph(csproj, project, options.Root, options);
            graph.Metadata.BuiltAtCommit = GitHead(options.Root);
            graph.Metadata.BuildMilliseconds = stopwatch.ElapsedMilliseconds;

            Directory.CreateDirectory(options.Out);
            File.WriteAllText(GraphPath(options), graph.ToJson());
            File.WriteAllText(Path.Combine(options.Out, "GRAPH_REPORT.md"), GraphReport.Build(graph));
            if (options.Html)
                File.WriteAllText(Path.Combine(options.Out, "graph.html"), GraphHtml.Build(graph));

            foreach (var file in AssistantSetup.OutdatedBlocks(options.Root, ToolPath()))
                Console.Error.WriteLine($"note: {Relative(file, options.Root)} was written by another version of entitas-graph install — run 'install' to update it.");

            var counts = graph.Nodes.GroupBy(n => n.Kind).ToDictionary(g => g.Key, g => g.Count());
            if (Get(counts, NodeKind.Component) == 0)
                Console.Error.WriteLine($"note: {Path.GetFileName(csproj)} declares contexts but no components were found in it.");
            Console.Error.WriteLine(
                $"Graph of {graph.Metadata.Project}: {Get(counts, NodeKind.Component)} components, {Get(counts, NodeKind.System)} systems, " +
                $"{Get(counts, NodeKind.Feature)} features, {Get(counts, NodeKind.Type)} other types, {graph.Edges.Count} edges " +
                $"in {graph.Metadata.BuildMilliseconds} ms → {Relative(options.Out, options.Root)}");
            return graph;
        }

        /// <summary>
        /// The graph of one version of the project: the csproj's references and defines with the
        /// given sources, rooted at <paramref name="root"/> (the Unity root, or a snapshot of it).
        /// Writes nothing, so it builds the old side of a diff as well as the current graph.
        /// </summary>
        static EcsGraph BuildGraph(string csproj, ProjectLoader.ProjectInfo project, string root, Options options)
        {
            var compilation = ProjectLoader.BuildCompilation(csproj, project);

            // A reference whose file is gone (no Library/ yet, a csproj from another machine) is
            // dropped by the loader; without Entitas.dll nothing is a component and the graph
            // would come out empty instead of wrong-looking.
            if (compilation.GetTypeByMetadataName("Entitas.IComponent") == null)
                throw new InvalidOperationException(
                    $"{Path.GetFileName(csproj)} does not reference a readable Entitas.dll, so no component can be recognised. " +
                    "Open the project in Unity once (it fills Library/ and rewrites the .csproj files), then run again.");

            // File mode with a csproj Unity has not rewritten since the last generation: take
            // every generated file in the output folder, or part of the API would be missing.
            var generationOptions = Entitas.SourceGenerator.EntitasGenerators.ReadOptions(compilation);
            if (generationOptions.Output == Entitas.SourceGenerator.GenerationOutput.Files &&
                Entitas.SourceGenerator.GeneratedFiles.OutputDirectory(compilation, generationOptions, root) is { } outputDirectory)
            {
                var complete = GenerationHost.WithGeneratedFiles(project, outputDirectory);
                if (complete.SourceFiles.Count != project.SourceFiles.Count)
                    compilation = ProjectLoader.BuildCompilation(csproj, complete);
            }

            var graph = GraphBuilder.Build(compilation, root, new GraphOptions
            {
                RequestSuffix = options.RequestSuffix,
                EventSuffix = options.EventSuffix
            });
            graph.Metadata.ToolVersion = Version();
            return graph;
        }

        // -- comparing ----------------------------------------------------------------------

        static string Diff(Options options)
        {
            DiffRequest request;
            try
            {
                request = DiffRequest.From(options.Refs, options.Commit, options.Positional);
            }
            catch (DiffUsageException ex)
            {
                throw new UsageException(ex.Message);
            }

            if (request.Commit != null)
                request = request.WithParents(RefSnapshot.Parents(options.Root, request.Commit).Parents);

            // Saved graphs first, so a mistyped path fails before anything is built.
            var sides = new[] { request.Old, request.New };
            var graphs = sides.Where(s => s.Source == DiffSource.GraphFile).ToDictionary(s => s, s => Load(s.Value));
            if (sides.Contains(DiffSide.WorkingTree))
                graphs[DiffSide.WorkingTree] = LoadOrBuild(options);
            foreach (var built in BuildAtRefs(options, sides.Where(s => s.Source == DiffSource.GitRef).ToList()))
                graphs[built.Key] = built.Value;

            return GraphDiff.Compare(graphs[request.Old], graphs[request.New])
                .Render(request.Old.Label, request.New.Label, options.Budget, request.Note);
        }

        static EcsGraph Load(string path) =>
            File.Exists(path) ? EcsGraph.FromJson(File.ReadAllText(path)) : throw new UsageException($"graph not found: {Path.GetFullPath(path)}");

        /// <summary>
        /// The graphs of the project at one or two git refs. Each ref gets its own snapshot and
        /// compilation, and today's csproj is mapped onto each commit separately — so a file
        /// added between the two refs is compiled on one side only, as it should be. References
        /// and defines come from today's csproj for both sides (see <see cref="RefSnapshot"/>).
        ///
        /// Two refs are built in parallel: the builds share nothing but read-only inputs (the
        /// csproj, the reference DLLs), and the generator's [ThreadStatic] flags are set by each
        /// build on its own thread before use. Git, extraction and the single-threaded stretches
        /// of one build overlap with the other's parallel ones.
        /// </summary>
        static Dictionary<DiffSide, EcsGraph> BuildAtRefs(Options options, IReadOnlyList<DiffSide> sides)
        {
            var graphs = new Dictionary<DiffSide, EcsGraph>();
            if (sides.Count == 0) return graphs;

            var stopwatch = Stopwatch.StartNew();
            var csproj = FindProject(options);
            var project = ProjectLoader.ParseProject(csproj);
            if (sides.Count == 1)
            {
                graphs[sides[0]] = BuildAtRef(options, csproj, project, sides[0]);
                return graphs;
            }

            var builds = sides.Select(side => Task.Run(() => BuildAtRef(options, csproj, project, side))).ToArray();
            // WhenAll waits for both, so neither snapshot outlives the command, and rethrows the
            // first failure as itself instead of an AggregateException.
            Task.WhenAll(builds).GetAwaiter().GetResult();
            for (var i = 0; i < sides.Count; i++)
                graphs[sides[i]] = builds[i].Result;
            Console.Error.WriteLine($"Both graphs built in parallel in {stopwatch.ElapsedMilliseconds} ms.");
            return graphs;
        }

        /// <summary>The graph of the project as it is at a commit, built from a temporary snapshot of its sources.</summary>
        static EcsGraph BuildAtRef(Options options, string csproj, ProjectLoader.ProjectInfo project, DiffSide side)
        {
            var stopwatch = Stopwatch.StartNew();
            using var snapshot = RefSnapshot.Extract(options.Root, project, side.Value);
            var graph = BuildGraph(csproj, snapshot.Project, snapshot.Root, options);
            graph.Metadata.BuiltAtCommit = snapshot.Commit;
            graph.Metadata.BuildMilliseconds = stopwatch.ElapsedMilliseconds;
            Console.Error.WriteLine(
                $"Graph at {side.Label} ({snapshot.Commit.Substring(0, Math.Min(10, snapshot.Commit.Length))}) from {snapshot.Project.SourceFiles.Count} files: " +
                $"{Path.GetFileName(csproj)} lists {snapshot.DroppedFiles} the commit does not have, the commit has {snapshot.AddedFiles} it does not list " +
                $"(generated, or deleted since) — {graph.Metadata.BuildMilliseconds} ms");
            return graph;
        }

        static int Get(Dictionary<string, int> counts, string key) => counts.TryGetValue(key, out var value) ? value : 0;

        static EcsGraph LoadOrBuild(Options options)
        {
            var path = GraphPath(options);
            if (File.Exists(path) && !IsStale(path, options.Root))
                return EcsGraph.FromJson(File.ReadAllText(path));

            Console.Error.WriteLine(File.Exists(path) ? "graph.json is older than the sources — rebuilding." : "No graph.json yet — building it.");
            return Build(options);
        }

        /// <summary>Any .cs under Assets/ (or the root) newer than graph.json.</summary>
        static bool IsStale(string graphPath, string root)
        {
            var built = File.GetLastWriteTimeUtc(graphPath);
            var assets = Path.Combine(root, "Assets");
            var folder = Directory.Exists(assets) ? assets : root;
            try
            {
                return Directory.EnumerateFiles(folder, "*.cs", SearchOption.AllDirectories)
                    .Any(file => File.GetLastWriteTimeUtc(file) > built);
            }
            catch (IOException)
            {
                return false;
            }
        }

        static string FindProject(Options options)
        {
            if (options.Project != null)
            {
                var explicitPath = Path.GetFullPath(options.Project);
                if (!File.Exists(explicitPath)) throw new UsageException($"csproj not found: {explicitPath}");
                return explicitPath;
            }

            var found = GenerationHost.FindGeneratingProjects(options.Root);
            if (found.Count == 1) return found[0];
            if (found.Count > 1)
                throw new UsageException("several projects declare contexts — pick one with --project: " +
                                         string.Join(", ", found.Select(Path.GetFileName)));

            throw new UsageException(
                $"no project under {options.Root} declares [assembly: ContextDefinition(\"…\")]. Run from the Unity project root " +
                "after Unity has written its .csproj files (any IDE package does), or pass --project.");
        }

        static string GraphPath(Options options) => Path.Combine(options.Out, "graph.json");

        static string Version() =>
            typeof(Program).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion.Split('+')[0] ?? "dev";

        static string? GitHead(string root)
        {
            try
            {
                var info = new ProcessStartInfo("git", "rev-parse HEAD")
                {
                    WorkingDirectory = root, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false
                };
                using var process = Process.Start(info);
                if (process == null) return null;
                var head = process.StandardOutput.ReadToEnd().Trim();
                process.WaitForExit();
                return process.ExitCode == 0 && head.Length > 0 ? head : null;
            }
            catch (Exception)
            {
                return null;
            }
        }

        static string Relative(string path, string root)
        {
            var relative = Path.GetRelativePath(root, path);
            return relative.StartsWith("..", StringComparison.Ordinal) ? path : relative;
        }

        sealed class UsageException : Exception
        {
            public UsageException(string message) : base(message) { }
        }

        sealed class Options
        {
            public string Command = string.Empty;
            public readonly List<string> Positional = new List<string>();
            // The wrappers install writes point here through ENTITAS_GRAPH_ROOT; -r still wins.
            public string Root = Environment.GetEnvironmentVariable(Setup.Wrappers.RootVariable) is { Length: > 0 } fromWrapper
                ? Path.GetFullPath(fromWrapper)
                : Directory.GetCurrentDirectory();
            public string? Project;
            public string? OutOverride;
            public string RequestSuffix = "Request";
            public string EventSuffix = "Event";
            public int Limit = GraphQueries.DefaultQuerySeeds;
            public int? Depth;
            public int Budget = GraphQueries.DefaultBudget;
            public bool Html = true;
            public readonly List<string> Refs = new List<string>();
            public string? Commit;
            public readonly List<string> Targets = new List<string>();
            public bool Gitignore = true;
            public bool ClaudeHook = true;
            public bool Mcp = true;

            public string Out => OutOverride ?? Path.Combine(Root, "entitas-graph-out");

            public string Arg(int index) =>
                index < Positional.Count ? Positional[index] : throw new UsageException($"'{Command}' needs {index + 1} argument(s)");

            public string Words() =>
                Positional.Count > 0 ? string.Join(" ", Positional) : throw new UsageException($"'{Command}' needs at least one word");

            /// <summary>--depth means steps for query and system hops for flow, so each command has its own default and range.</summary>
            public int DepthFor(int fallback, int max) =>
                Depth == null ? fallback
                : Depth <= max ? Depth.Value
                : throw new UsageException($"--depth for '{Command}' is 1 to {max}");

            public static Options? Parse(string[] args)
            {
                if (args.Length == 0 || args[0] is "-h" or "--help")
                    return null;

                var options = new Options { Command = args[0] };
                for (var i = 1; i < args.Length; i++)
                {
                    switch (args[i])
                    {
                        case "-r": case "--root":
                            if (++i >= args.Length) return null;
                            options.Root = Path.GetFullPath(args[i]);
                            break;
                        case "-p": case "--project":
                            if (++i >= args.Length) return null;
                            options.Project = args[i];
                            break;
                        case "-o": case "--out":
                            if (++i >= args.Length) return null;
                            options.OutOverride = Path.GetFullPath(args[i]);
                            break;
                        case "--request-suffix":
                            if (++i >= args.Length) return null;
                            options.RequestSuffix = args[i];
                            break;
                        case "--event-suffix":
                            if (++i >= args.Length) return null;
                            options.EventSuffix = args[i];
                            break;
                        case "--limit":
                            if (++i >= args.Length || !int.TryParse(args[i], out options.Limit) || options.Limit < 1) return null;
                            break;
                        case "--depth":
                            if (++i >= args.Length || !int.TryParse(args[i], out var depth) || depth < 1) return null;
                            options.Depth = depth;
                            break;
                        case "--budget":
                            if (++i >= args.Length || !int.TryParse(args[i], out options.Budget) || options.Budget < 0) return null;
                            break;
                        case "--no-html":
                            options.Html = false;
                            break;
                        case "--ref":
                            // Repeatable: diff checks there are at most two (old, then new).
                            if (++i >= args.Length) return null;
                            options.Refs.Add(args[i]);
                            break;
                        case "--commit":
                            if (++i >= args.Length) return null;
                            if (options.Commit != null) throw new UsageException("--commit takes one commit");
                            options.Commit = args[i];
                            break;
                        case "--target":
                            if (++i >= args.Length) return null;
                            options.Targets.Add(args[i]);
                            break;
                        case "--no-gitignore":
                            options.Gitignore = false;
                            break;
                        case "--no-claude-hook":
                            options.ClaudeHook = false;
                            break;
                        case "--no-mcp":
                            options.Mcp = false;
                            break;
                        case "-h": case "--help":
                            return null;
                        default:
                            if (args[i].StartsWith("-", StringComparison.Ordinal)) return null;
                            options.Positional.Add(args[i]);
                            break;
                    }
                }

                return options;
            }
        }
    }
}
