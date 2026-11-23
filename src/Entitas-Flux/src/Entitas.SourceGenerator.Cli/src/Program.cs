using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace Entitas.SourceGenerator.Cli
{
    /// <summary>
    /// entitas-gen: writes the Entitas-Flux generated code to .cs files for assemblies that
    /// set <c>[assembly: EntitasGeneration(Output = GenerationOutput.Files)]</c>, using the
    /// very generator engine the compile-time source generator runs.
    ///
    /// The Unity editor runs it (Tools > Entitas > Generate, and automatically when a
    /// component changes) through --request; people and CI run it from a terminal.
    /// </summary>
    internal static class Program
    {
        static int Main(string[] args)
        {
            try
            {
                var options = Options.Parse(args);
                if (options == null)
                {
                    PrintUsage();
                    return 2;
                }

                if (options.RequestPath != null)
                    return RunRequest(options);

                if (options.LegacyOutputDirectory != null)
                    return RunLegacy(options);

                return RunProjects(options);
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"error: {ex.Message}");
                return 1;
            }
        }

        static void PrintUsage()
        {
            Console.Error.WriteLine("Usage: entitas-gen [options]");
            Console.Error.WriteLine();
            Console.Error.WriteLine("Writes the generated Entitas code to .cs files for every assembly that sets");
            Console.Error.WriteLine("[assembly: EntitasGeneration(Output = GenerationOutput.Files)]. Only changed files are");
            Console.Error.WriteLine("rewritten; files no longer generated are deleted with their .meta.");
            Console.Error.WriteLine();
            Console.Error.WriteLine("  -r, --root <dir>        Unity project root (default: current directory).");
            Console.Error.WriteLine("  -p, --project <csproj>  Generate for this project only (repeatable). Default: every");
            Console.Error.WriteLine("                          csproj in the root that declares [assembly: ContextDefinition].");
            Console.Error.WriteLine("      --check             Write nothing; exit 1 if any generated file is out of date.");
            Console.Error.WriteLine("      --apply-renames     A component was renamed in the IDE (Shift+F6) and code still uses");
            Console.Error.WriteLine("                          its old generated API (AddHealth, hasHealth, …): update those usages,");
            Console.Error.WriteLine("                          then regenerate. Without it such a rename is printed and nothing is");
            Console.Error.WriteLine("                          written (exit 1).");
            Console.Error.WriteLine("      --ignore-renames    Regenerate anyway, leaving those usages to break.");
            Console.Error.WriteLine("  -v, --verbose           List every file written or deleted.");
            Console.Error.WriteLine();
            Console.Error.WriteLine("  entitas-gen <csproj> <output-dir>   Write into <output-dir> whatever the Output setting.");
        }

        static int RunProjects(Options options)
        {
            var stopwatch = Stopwatch.StartNew();
            var root = options.Root;

            var projects = options.Projects.Count > 0
                ? options.Projects.Select(Path.GetFullPath).ToList()
                : GenerationHost.FindGeneratingProjects(root).ToList();

            if (projects.Count == 0)
            {
                Console.Error.WriteLine(
                    $"error: no project under {root} declares [assembly: ContextDefinition(\"…\")]. Run this from the " +
                    "Unity project root after Unity has written its .csproj files (any IDE package does), or pass --project.");
                return 1;
            }

            var failed = false;
            foreach (var csproj in projects)
            {
                if (!File.Exists(csproj))
                {
                    Console.Error.WriteLine($"error: csproj not found: {csproj}");
                    failed = true;
                    continue;
                }

                var result = GenerationHost.RunProject(csproj, root, new WriterOptions
                {
                    Check = options.Check,
                    Renames = options.Renames
                });
                GenerationHost.Print(result, root, options.Verbose, Console.Out);
                failed |= GenerationHost.Failed(result, options.Check);
            }

            if (options.Check && failed)
                Console.WriteLine("Run entitas-gen (or Tools > Entitas > Generate in Unity) to update them.");

            if (options.Verbose)
                Console.WriteLine($"Done in {stopwatch.ElapsedMilliseconds} ms.");

            return failed ? 1 : 0;
        }

        /// <summary>The original two-argument form, kept for scripts that use it — now without wiping the folder.</summary>
        static int RunLegacy(Options options)
        {
            var csproj = Path.GetFullPath(options.Projects[0]);
            if (!File.Exists(csproj))
            {
                Console.Error.WriteLine($"error: csproj not found: {csproj}");
                return 1;
            }

            var root = Path.GetDirectoryName(csproj)!;
            var result = GenerationHost.RunProject(csproj, root, new WriterOptions
            {
                Check = options.Check,
                OutputDirectoryOverride = options.LegacyOutputDirectory,
                Renames = options.Renames
            });

            GenerationHost.Print(result, root, options.Verbose, Console.Out);
            return GenerationHost.Failed(result, options.Check) ? 1 : 0;
        }

        static readonly JsonSerializerOptions Json = new JsonSerializerOptions
        {
            IncludeFields = true,
            PropertyNameCaseInsensitive = true,
            WriteIndented = true
        };

        static int RunRequest(Options options)
        {
            GenerationResponse response;
            try
            {
                var request = JsonSerializer.Deserialize<GenerationRequest>(File.ReadAllText(options.RequestPath!), Json)
                              ?? throw new InvalidOperationException("empty request");
                response = GenerationHost.RunRequest(request);
            }
            catch (Exception ex)
            {
                response = new GenerationResponse { error = ex.Message };
            }

            var json = JsonSerializer.Serialize(response, Json);
            if (options.ResponsePath != null)
                File.WriteAllText(options.ResponsePath, json);
            else
                Console.WriteLine(json);

            return response.error.Length > 0 || response.assemblies.Any(a => a.errors.Count > 0) ? 1 : 0;
        }

        sealed class Options
        {
            public string Root = Directory.GetCurrentDirectory();
            public readonly List<string> Projects = new List<string>();
            public bool Check;
            public bool Verbose;
            public RenameHandling Renames = RenameHandling.Ask;
            public string? RequestPath;
            public string? ResponsePath;
            public string? LegacyOutputDirectory;

            public static Options? Parse(string[] args)
            {
                var options = new Options();
                var positional = new List<string>();

                for (var i = 0; i < args.Length; i++)
                {
                    switch (args[i])
                    {
                        case "-r":
                        case "--root":
                            if (++i >= args.Length) return null;
                            options.Root = Path.GetFullPath(args[i]);
                            break;
                        case "-p":
                        case "--project":
                            if (++i >= args.Length) return null;
                            options.Projects.Add(args[i]);
                            break;
                        case "--check":
                            options.Check = true;
                            break;
                        case "--apply-renames":
                            if (options.Renames == RenameHandling.Ignore) return null;
                            options.Renames = RenameHandling.Apply;
                            break;
                        case "--ignore-renames":
                            if (options.Renames == RenameHandling.Apply) return null;
                            options.Renames = RenameHandling.Ignore;
                            break;
                        case "-v":
                        case "--verbose":
                            options.Verbose = true;
                            break;
                        case "--request":
                            if (++i >= args.Length) return null;
                            options.RequestPath = args[i];
                            break;
                        case "--response":
                            if (++i >= args.Length) return null;
                            options.ResponsePath = args[i];
                            break;
                        case "-h":
                        case "--help":
                            return null;
                        default:
                            if (args[i].StartsWith("-", StringComparison.Ordinal))
                                return null;
                            positional.Add(args[i]);
                            break;
                    }
                }

                if (positional.Count == 2 && options.Projects.Count == 0)
                {
                    options.Projects.Add(positional[0]);
                    options.LegacyOutputDirectory = Path.GetFullPath(positional[1]);
                    return options;
                }

                return positional.Count == 0 ? options : null;
            }
        }
    }
}
