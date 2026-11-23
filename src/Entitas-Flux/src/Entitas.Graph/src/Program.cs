using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using Entitas.Graph.Analysis;
using Entitas.Graph.Output;
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
            try
            {
                var options = Options.Parse(args);
                if (options == null)
                {
                    PrintUsage();
                    return Usage;
                }

                if (options.Command == "build")
                {
                    Build(options);
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
                    "query" => queries.Query(options.Words(), options.Limit, options.Depth),
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
            catch (AmbiguousNameException ex)
            {
                Console.Error.WriteLine($"{ex.Message} — be more specific:");
                foreach (var node in ex.Candidates.OrderBy(n => n.Label))
                    Console.Error.WriteLine($"  {node.FullName ?? node.Id}  [{node.Kind}]");
                return Ambiguous;
            }
            catch (NodeNotFoundException ex)
            {
                Console.Error.WriteLine(ex.Message + (ex.Suggestions.Count > 0 ? ". Did you mean:" : "."));
                foreach (var node in ex.Suggestions)
                    Console.Error.WriteLine($"  {node.Label}  [{node.Kind}]");
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
            Console.Error.WriteLine("  report                      Print GRAPH_REPORT.md (findings).");
            Console.Error.WriteLine();
            Console.Error.WriteLine("  -r, --root <dir>            Unity project root (default: current directory).");
            Console.Error.WriteLine("  -p, --project <csproj>      The project declaring the contexts (default: found under the root).");
            Console.Error.WriteLine("  -o, --out <dir>             Output folder (default: <root>/entitas-graph-out).");
            Console.Error.WriteLine("      --request-suffix <s>    Name suffix of request components (default: Request).");
            Console.Error.WriteLine("      --event-suffix <s>      Name suffix of event components (default: Event).");
            Console.Error.WriteLine("      --budget <tokens>       Cap each answer at about this many tokens (default: 2000; 0 = no cap).");
            Console.Error.WriteLine("      --limit <n>             Seed nodes for query (default: 5).");
            Console.Error.WriteLine("      --depth <n>             Steps query walks from the seeds (1-4, default: 2).");
            Console.Error.WriteLine("      --no-html               Skip graph.html on build.");
            Console.Error.WriteLine();
            Console.Error.WriteLine("Queries reuse graph.json and rebuild it first when a source file is newer. An answer over");
            Console.Error.WriteLine("the budget starts with a summary by feature and says how much it left out.");
            Console.Error.WriteLine("Exit codes: 0 answer, 1 error, 2 usage, 3 ambiguous name, 4 not found.");
        }

        // -- building -----------------------------------------------------------------------

        static EcsGraph Build(Options options)
        {
            var stopwatch = Stopwatch.StartNew();
            var csproj = FindProject(options);
            var project = ProjectLoader.ParseProject(csproj);
            if (project.MissingSourceFiles.Count > 0)
                Console.Error.WriteLine($"note: {Path.GetFileName(csproj)} lists {project.MissingSourceFiles.Count} file(s) that no longer exist — let Unity regenerate it if something seems missing.");

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
                Entitas.SourceGenerator.GeneratedFiles.OutputDirectory(compilation, generationOptions, options.Root) is { } outputDirectory)
            {
                var complete = GenerationHost.WithGeneratedFiles(project, outputDirectory);
                if (complete.SourceFiles.Count != project.SourceFiles.Count)
                    compilation = ProjectLoader.BuildCompilation(csproj, complete);
            }

            var graph = GraphBuilder.Build(compilation, options.Root, new GraphOptions
            {
                RequestSuffix = options.RequestSuffix,
                EventSuffix = options.EventSuffix
            });
            graph.Metadata.ToolVersion = Version();
            graph.Metadata.BuiltAtCommit = GitHead(options.Root);
            graph.Metadata.BuildMilliseconds = stopwatch.ElapsedMilliseconds;

            Directory.CreateDirectory(options.Out);
            File.WriteAllText(GraphPath(options), graph.ToJson());
            File.WriteAllText(Path.Combine(options.Out, "GRAPH_REPORT.md"), GraphReport.Build(graph));
            if (options.Html)
                File.WriteAllText(Path.Combine(options.Out, "graph.html"), GraphHtml.Build(graph));

            var counts = graph.Nodes.GroupBy(n => n.Kind).ToDictionary(g => g.Key, g => g.Count());
            if (Get(counts, NodeKind.Component) == 0)
                Console.Error.WriteLine($"note: {Path.GetFileName(csproj)} declares contexts but no components were found in it.");
            Console.Error.WriteLine(
                $"Graph of {graph.Metadata.Project}: {Get(counts, NodeKind.Component)} components, {Get(counts, NodeKind.System)} systems, " +
                $"{Get(counts, NodeKind.Feature)} features, {Get(counts, NodeKind.Type)} other types, {graph.Edges.Count} edges " +
                $"in {graph.Metadata.BuildMilliseconds} ms → {Relative(options.Out, options.Root)}");
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
            public string Root = Directory.GetCurrentDirectory();
            public string? Project;
            public string? OutOverride;
            public string RequestSuffix = "Request";
            public string EventSuffix = "Event";
            public int Limit = GraphQueries.DefaultQuerySeeds;
            public int Depth = GraphQueries.DefaultQueryDepth;
            public int Budget = GraphQueries.DefaultBudget;
            public bool Html = true;

            public string Out => OutOverride ?? Path.Combine(Root, "entitas-graph-out");

            public string Arg(int index) =>
                index < Positional.Count ? Positional[index] : throw new UsageException($"'{Command}' needs {index + 1} argument(s)");

            public string Words() =>
                Positional.Count > 0 ? string.Join(" ", Positional) : throw new UsageException($"'{Command}' needs at least one word");

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
                            if (++i >= args.Length || !int.TryParse(args[i], out options.Depth) ||
                                options.Depth < 1 || options.Depth > GraphQueries.MaxQueryDepth) return null;
                            break;
                        case "--budget":
                            if (++i >= args.Length || !int.TryParse(args[i], out options.Budget) || options.Budget < 0) return null;
                            break;
                        case "--no-html":
                            options.Html = false;
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
