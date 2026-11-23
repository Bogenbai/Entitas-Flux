using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using Entitas.SourceGenerator;
using Entitas.SourceGenerator.Cli;
using Entitas.SourceGenerator.Rename;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace Entitas.Rename
{
    /// <summary>
    /// Renames an Entitas component and every identifier the source generator derives
    /// from its name (matcher, lookup, entity/context API, watched and event members)
    /// across a Unity project. Dry run by default; --apply writes.
    /// </summary>
    internal static class Program
    {
        static int Main(string[] args)
        {
            try
            {
                var options = CommandLineOptions.Parse(args);
                if (options == null)
                {
                    PrintUsage();
                    return 2;
                }

                var csprojPath = ResolveProjectPath(options);
                Console.WriteLine($"Project:   {Relative(csprojPath, options.Root)}");

                var project = ProjectLoader.ParseProject(csprojPath);
                if (project.SourceFiles.Count == 0)
                    throw new RenameException($"no <Compile> source files found in {csprojPath}");

                if (project.MissingSourceFiles.Count > 0)
                {
                    Console.WriteLine(
                        $"           note: the csproj lists {project.MissingSourceFiles.Count} file(s) that no longer " +
                        "exist — it is stale. Let Unity regenerate it if a component seems missing.");
                }

                var compilation = ProjectLoader.BuildCompilation(csprojPath, project);
                var projectRoot = Path.GetDirectoryName(csprojPath)!;

                // File mode: the plan resolves usages against the generated files on disk,
                // so they must be current — regenerate first if they are not.
                var filesMode = EntitasGenerators.ReadOptions(compilation).Output == GenerationOutput.Files;
                if (filesMode)
                {
                    var check = GeneratedFileWriter.Run(compilation, projectRoot, new WriterOptions { Check = true });
                    if (check.Staleness != null && !check.Staleness.IsUpToDate)
                    {
                        if (options.Apply)
                        {
                            Console.WriteLine("           note: generated files were out of date — regenerating them first.");
                            var regenerated = Regenerate(csprojPath, project, projectRoot);
                            if (regenerated.Rename?.Outcome == RenameOutcome.Pending)
                                throw new RenameException(
                                    $"{regenerated.Rename.OldClassName} was renamed to {regenerated.Rename.NewClassName} in the IDE and " +
                                    "its old generated API is still in use. Regenerate first — Tools > Entitas > Generate in Unity, " +
                                    "or entitas-gen --apply-renames (or --ignore-renames) — then rename again.");
                        }
                        else
                        {
                            Console.WriteLine(
                                $"           note: generated files are out of date ({check.Staleness.Describe()}); " +
                                "-a regenerates them before renaming.");
                        }
                    }

                    // A csproj Unity has not rewritten since the last generation does not list
                    // every generated file; usages are resolved against them, so take them all.
                    if (check.OutputDirectory != null)
                        project = GenerationHost.WithGeneratedFiles(project, check.OutputDirectory);

                    compilation = ProjectLoader.BuildCompilation(csprojPath, project);
                }

                var plan = RenameEngine.CreatePlan(
                    compilation,
                    options.OldName,
                    options.NewName,
                    new RenameOptions { IncludeStrings = options.IncludeStrings });

                var external = GenerationHost.ReferencingEdits(csprojPath, compilation, plan);

                PrintPlan(plan, external, options);

                if (!options.Apply)
                {
                    Console.WriteLine();
                    Console.WriteLine(plan.EditCount + external.Sum(e => e.EditCount) == 0
                        ? "Nothing to do."
                        : "Dry run — nothing written. Re-run with -a to rename.");
                    return 0;
                }

                var (written, movedTo) = ApplyPlan(plan, external, options);
                Console.WriteLine();
                var total = plan.EditCount + external.Sum(e => e.EditCount);
                Console.WriteLine($"Renamed {plan.OldClassName} -> {plan.NewClassName} " +
                                  $"({total} replacements in {written} files). Review with `git diff`.");

                if (filesMode)
                {
                    // The csproj still lists the component under its old file name; point the
                    // project at the moved file, or regeneration would not see the component
                    // and would delete its generated code instead of renaming it.
                    if (movedTo != null)
                        project = WithMovedFile(project, plan.DeclarationFile!, movedTo);

                    var regenerated = Regenerate(csprojPath, project, projectRoot);
                    if (regenerated.Errors.Count > 0)
                        return 1;
                }

                if (movedTo != null)
                {
                    Console.WriteLine(
                        "The csproj still points at the old file path — reopen the project in Unity " +
                        "before renaming another component.");
                }
                return 0;
            }
            catch (RenameException ex)
            {
                Console.Error.WriteLine($"error: {ex.Message}");
                return 1;
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"error: {ex.Message}");
                return 1;
            }
        }

        static void PrintUsage()
        {
            Console.Error.WriteLine("Usage: entitas-rename <OldName> <NewName> [options]");
            Console.Error.WriteLine();
            Console.Error.WriteLine("  <OldName>            Component to rename (Health or HealthComponent,");
            Console.Error.WriteLine("                       or My.Game.HealthComponent to disambiguate).");
            Console.Error.WriteLine("  <NewName>            New name, same form.");
            Console.Error.WriteLine();
            Console.Error.WriteLine("  -a, --apply             Write the changes (default: dry run).");
            Console.Error.WriteLine("  -r, --root <dir>        Where to look for the csproj (default: current dir).");
            Console.Error.WriteLine("  -p, --project <csproj>  Use this csproj instead of searching for one.");
            Console.Error.WriteLine("  -s, --include-strings   Also rewrite string literals and comments.");
            Console.Error.WriteLine("  -k, --keep-file-name    Do not rename the component's .cs (and .cs.meta) file.");
        }

        // -- project discovery ------------------------------------------------

        static string ResolveProjectPath(CommandLineOptions options)
        {
            if (options.ProjectPath != null)
            {
                var explicitPath = Path.GetFullPath(options.ProjectPath);
                if (!File.Exists(explicitPath))
                    throw new RenameException($"csproj not found: {explicitPath}");

                return explicitPath;
            }

            var root = options.Root;
            var preferred = Path.Combine(root, "Assembly-CSharp.csproj");
            if (File.Exists(preferred))
                return preferred;

            var nested = Directory
                .EnumerateFiles(root, "Assembly-CSharp.csproj", SearchOption.AllDirectories)
                .Where(p => !IsIgnoredDirectory(p))
                .Take(2)
                .ToArray();
            if (nested.Length == 1)
                return nested[0];

            var any = Directory.EnumerateFiles(root, "*.csproj", SearchOption.TopDirectoryOnly).Take(2).ToArray();
            if (any.Length == 1)
                return any[0];

            throw new RenameException(
                $"could not find a csproj under {root}. Open the project in Unity once so it writes " +
                "Assembly-CSharp.csproj, or pass --project <path>.");
        }

        static bool IsIgnoredDirectory(string path) =>
            path.Contains($"{Path.DirectorySeparatorChar}Library{Path.DirectorySeparatorChar}") ||
            path.Contains($"{Path.DirectorySeparatorChar}Temp{Path.DirectorySeparatorChar}") ||
            path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}");

        // -- output -----------------------------------------------------------

        static void PrintPlan(RenamePlan plan, List<ExternalEdits> external, CommandLineOptions options)
        {
            var contexts = plan.ContextNames.Length > 0
                ? $"  [{string.Join(", ", plan.ContextNames)}]"
                : string.Empty;

            Console.WriteLine($"Component: {plan.OldFullTypeName}{contexts}");
            Console.WriteLine();
            Console.WriteLine($"Identifiers ({plan.NameMap.Count}):");
            var width = plan.NameMap.Keys.Select(k => k.Length).DefaultIfEmpty(0).Max();
            foreach (var pair in plan.NameMap.OrderBy(p => p.Key, StringComparer.Ordinal))
                Console.WriteLine($"  {pair.Key.PadRight(width)} -> {pair.Value}");

            if (plan.Files.Count > 0)
            {
                Console.WriteLine();
                Console.WriteLine($"Usages ({plan.EditCount} in {plan.Files.Count} files):");
                foreach (var file in plan.Files.OrderBy(f => f.Path, StringComparer.Ordinal))
                {
                    Console.WriteLine($"  {Relative(file.Path, options.Root)}");
                    foreach (var group in file.Edits.GroupBy(e => e.Line).OrderBy(g => g.Key))
                    {
                        var replacements = group
                            .Select(e => $"{e.OldText} -> {e.NewText}")
                            .Distinct(StringComparer.Ordinal);
                        Console.WriteLine($"    {group.Key,5}  {string.Join(", ", replacements)}");
                    }
                }
            }

            foreach (var assembly in external)
            {
                Console.WriteLine();
                Console.WriteLine($"Usages in {assembly.AssemblyName} ({assembly.EditCount} in {assembly.Files.Count} files):");
                foreach (var file in assembly.Files.OrderBy(f => f.Path, StringComparer.Ordinal))
                {
                    Console.WriteLine($"  {Relative(file.Path, options.Root)}");
                    foreach (var group in file.Edits.GroupBy(e => e.Line).OrderBy(g => g.Key))
                    {
                        var replacements = group
                            .Select(e => $"{e.OldText} -> {e.NewText}")
                            .Distinct(StringComparer.Ordinal);
                        Console.WriteLine($"    {group.Key,5}  {string.Join(", ", replacements)}");
                    }
                }
            }

            if (plan.DeclarationFile != null && !options.KeepFileName)
            {
                var target = RenamedFilePath(plan);
                if (target != null)
                {
                    Console.WriteLine();
                    Console.WriteLine("File:");
                    Console.WriteLine($"  {Relative(plan.DeclarationFile, options.Root)} -> " +
                                      $"{Path.GetFileName(target)} (with its .meta)");
                }
            }

            var warnings = plan.Warnings.Concat(external.SelectMany(e => e.Warnings)).ToArray();
            if (warnings.Length > 0)
            {
                Console.WriteLine();
                Console.WriteLine($"Warnings ({warnings.Length}):");
                foreach (var warning in warnings)
                    Console.WriteLine($"  ! {warning}");
            }
        }

        static string Relative(string path, string root)
        {
            try
            {
                var relative = Path.GetRelativePath(root, path);
                return relative.StartsWith("..", StringComparison.Ordinal) ? path : relative;
            }
            catch
            {
                return path;
            }
        }

        // -- apply ------------------------------------------------------------

        static AssemblyResult Regenerate(string csprojPath, ProjectLoader.ProjectInfo project, string projectRoot)
        {
            var compilation = ProjectLoader.BuildCompilation(csprojPath, project);
            var result = GeneratedFileWriter.Run(compilation, projectRoot, new WriterOptions());
            Console.Write("Generated: ");
            GenerationHost.Print(result, projectRoot, verbose: false, Console.Out);
            return result;
        }

        static ProjectLoader.ProjectInfo WithMovedFile(ProjectLoader.ProjectInfo project, string from, string to) =>
            new ProjectLoader.ProjectInfo(
                project.SourceFiles.Select(f => string.Equals(f, from, StringComparison.Ordinal) ? to : f).ToList(),
                project.MissingSourceFiles,
                project.References,
                project.Defines,
                project.LanguageVersion);

        static (int Written, string? MovedTo) ApplyPlan(
            RenamePlan plan,
            List<ExternalEdits> external,
            CommandLineOptions options)
        {
            var written = 0;
            string? moved = null;

            foreach (var file in plan.Files.Concat(external.SelectMany(e => e.Files)))
            {
                var original = File.ReadAllText(file.Path);
                var updated = RenameEngine.Apply(original, file);
                if (updated == original)
                    continue;

                File.WriteAllText(file.Path, updated);
                written++;
            }

            if (!options.KeepFileName)
            {
                var target = RenamedFilePath(plan);
                if (target != null)
                {
                    MoveFile(plan.DeclarationFile!, target);
                    moved = target;
                }
            }

            return (written, moved);
        }

        /// <summary>
        /// The component's file is renamed only when its name actually follows the class
        /// (Health.cs / HealthComponent.cs) — never when it holds several types.
        /// </summary>
        static string? RenamedFilePath(RenamePlan plan)
        {
            if (plan.DeclarationFile == null)
                return null;

            var directory = Path.GetDirectoryName(plan.DeclarationFile)!;
            var name = Path.GetFileNameWithoutExtension(plan.DeclarationFile);
            if (name != plan.OldClassName)
                return null;

            var target = Path.Combine(directory, plan.NewClassName + Path.GetExtension(plan.DeclarationFile));
            return File.Exists(target) ? null : target;
        }

        /// <summary>
        /// Moves the .cs and its Unity .meta sidecar together — losing the .meta would
        /// change the asset GUID and break every scene/prefab reference to it. Uses
        /// `git mv` when the file is tracked so history follows the rename.
        /// </summary>
        static void MoveFile(string from, string to)
        {
            var pairs = new List<(string From, string To)> { (from, to) };
            if (File.Exists(from + ".meta"))
                pairs.Add((from + ".meta", to + ".meta"));

            foreach (var (source, destination) in pairs)
            {
                if (!TryGitMove(source, destination))
                    File.Move(source, destination);

                Console.WriteLine($"  moved {Path.GetFileName(source)} -> {Path.GetFileName(destination)}");
            }
        }

        static bool TryGitMove(string from, string to)
        {
            try
            {
                var info = new ProcessStartInfo("git")
                {
                    WorkingDirectory = Path.GetDirectoryName(from)!,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true
                };
                info.ArgumentList.Add("mv");
                info.ArgumentList.Add(from);
                info.ArgumentList.Add(to);

                using var process = Process.Start(info);
                if (process == null)
                    return false;

                process.WaitForExit();
                return process.ExitCode == 0;
            }
            catch
            {
                return false;
            }
        }

        // -- args -------------------------------------------------------------

        sealed class CommandLineOptions
        {
            public string OldName = string.Empty;
            public string NewName = string.Empty;
            public string Root = Directory.GetCurrentDirectory();
            public string? ProjectPath;
            public bool Apply;
            public bool IncludeStrings;
            public bool KeepFileName;

            public static CommandLineOptions? Parse(string[] args)
            {
                var options = new CommandLineOptions();
                var positional = new List<string>();

                for (var i = 0; i < args.Length; i++)
                {
                    switch (args[i])
                    {
                        case "--apply":
                        case "-a":
                            options.Apply = true;
                            break;
                        case "--include-strings":
                        case "-s":
                            options.IncludeStrings = true;
                            break;
                        case "--keep-file-name":
                        case "-k":
                            options.KeepFileName = true;
                            break;
                        case "--root":
                        case "-r":
                            if (++i >= args.Length) return null;
                            options.Root = Path.GetFullPath(args[i]);
                            break;
                        case "--project":
                        case "-p":
                            if (++i >= args.Length) return null;
                            options.ProjectPath = args[i];
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

                if (positional.Count != 2)
                    return null;

                options.OldName = positional[0];
                options.NewName = positional[1];
                return options;
            }
        }
    }
}
