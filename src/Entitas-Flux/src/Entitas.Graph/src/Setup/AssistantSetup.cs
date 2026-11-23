using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;

namespace Entitas.Graph.Setup
{
    /// <summary>A setup request that cannot work as given (not a Unity root); exits like a usage error.</summary>
    public sealed class SetupException : Exception
    {
        public SetupException(string message) : base(message) { }
    }

    public enum FileOutcome { Created, Updated, Unchanged, Deleted }

    public sealed class FileChange
    {
        public string Path { get; }
        public FileOutcome Outcome { get; }
        public string? Detail { get; }

        public FileChange(string path, FileOutcome outcome, string? detail = null)
        {
            Path = path;
            Outcome = outcome;
            Detail = detail;
        }
    }

    public sealed class SetupResult
    {
        public List<FileChange> Files { get; } = new List<FileChange>();
        public List<string> Notes { get; } = new List<string>();
    }

    /// <summary>
    /// <c>entitas-graph install</c> / <c>uninstall</c>: the file side of <see cref="InstructionBlock"/>
    /// and <see cref="GitIgnore"/>. Needs neither the csproj nor a built graph — only where the
    /// Unity root, the repository and the tool are.
    /// </summary>
    public static class AssistantSetup
    {
        public const string ClaudeFile = "CLAUDE.md";
        public const string AgentsFile = "AGENTS.md";

        /// <param name="targets">Instruction files from --target; empty for the default (see <see cref="DefaultTargets"/>).</param>
        /// <param name="toolPath">Where entitas-graph.dll lives — injectable so tests do not depend on where they run.</param>
        public static SetupResult Install(string unityRoot, IReadOnlyList<string> targets, bool gitignore, string toolPath)
        {
            var unity = RequireUnityRoot(unityRoot);
            var gitRoot = FindGitRoot(unity);
            var repository = gitRoot ?? unity;
            var files = Targets(targets, repository);
            var result = new SetupResult();

            // Plan every file before writing any, so a malformed block leaves all of them as they were.
            var plan = new List<(string File, TextFile? Existing, string Updated)>();
            foreach (var file in files)
            {
                var existing = TextFile.Read(file);
                if (targets.Count == 0 && Path.GetFileName(file) == ClaudeFile && existing != null &&
                    files.Any(f => Path.GetFileName(f) == AgentsFile) && InstructionBlock.Imports(existing.Text, AgentsFile))
                {
                    result.Files.Add(new FileChange(file, FileOutcome.Unchanged, $"imports {AgentsFile}, which carries the block"));
                    continue;
                }

                var block = InstructionBlock.Build(Path.GetDirectoryName(file)!, unity, toolPath, repository);
                plan.Add((file, existing, Apply(file, () => InstructionBlock.Install(existing?.Text, block))));
            }

            // The wrappers the block names, in the repository root. A file of that name install did
            // not write is someone else's: stop before writing anything.
            var wrappers = new List<(string File, TextFile? Existing, string Updated)>();
            foreach (var (file, text) in WrapperTexts(repository, unity, toolPath))
            {
                var existing = TextFile.Read(file);
                if (existing != null && !Wrappers.IsOurs(existing.Text))
                    throw new SetupException($"{file} exists and was not written by entitas-graph install — rename or move it, then run install again.");
                wrappers.Add((file, existing, text));
            }

            foreach (var (file, existing, updated) in plan)
                result.Files.Add(Write(file, existing, updated));
            foreach (var (file, existing, updated) in wrappers)
            {
                result.Files.Add(Write(file, existing, updated));
                if (Path.GetFileName(file) == Wrappers.ShName && !MakeExecutable(file))
                    result.Notes.Add($"could not mark {file} executable — run chmod +x on it, or call it as sh {Wrappers.ShName}.");
            }

            if (!InstructionBlock.IsUnder(toolPath, repository))
                result.Notes.Add($"{Path.GetFileName(toolPath)} is outside {repository}, so the block names it by absolute path and works on this " +
                                 "machine only. Run install with the tool the project has (Library/PackageCache/… or Assets/…/Tools~) for a block the team can share.");

            if (gitignore)
            {
                if (gitRoot != null) result.Files.Add(Ignore(gitRoot, unity));
                else result.Notes.Add($"no git repository above {unity} — .gitignore left alone.");
            }

            return result;
        }

        public static SetupResult Uninstall(string unityRoot, IReadOnlyList<string> targets)
        {
            var unity = RequireUnityRoot(unityRoot);
            var repository = FindGitRoot(unity) ?? unity;
            var files = Targets(targets, repository);
            var result = new SetupResult();

            var plan = new List<(string File, TextFile? Existing, string? Updated)>();
            foreach (var file in files)
            {
                var existing = TextFile.Read(file);
                if (existing == null)
                    result.Files.Add(new FileChange(file, FileOutcome.Unchanged, "does not exist"));
                else
                    plan.Add((file, existing, Apply(file, () => InstructionBlock.Uninstall(existing.Text))));
            }

            foreach (var (file, existing, updated) in plan)
            {
                if (updated == null)
                {
                    result.Files.Add(new FileChange(file, FileOutcome.Unchanged, "has no entitas-graph block"));
                }
                else if (updated.Trim().Length == 0)
                {
                    File.Delete(file);
                    result.Files.Add(new FileChange(file, FileOutcome.Deleted, "nothing else was in it"));
                }
                else
                {
                    result.Files.Add(Write(file, existing, updated));
                }
            }

            foreach (var file in Wrappers.Files(repository))
            {
                var existing = TextFile.Read(file);
                if (existing == null) continue;
                if (Wrappers.IsOurs(existing.Text))
                {
                    File.Delete(file);
                    result.Files.Add(new FileChange(file, FileOutcome.Deleted));
                }
                else
                {
                    result.Files.Add(new FileChange(file, FileOutcome.Unchanged, "not written by install, left alone"));
                }
            }

            return result;
        }

        /// <summary>Each wrapper's file in the repository root and the text install writes into it.</summary>
        static IEnumerable<(string File, string Text)> WrapperTexts(string repository, string unity, string toolPath)
        {
            yield return (Path.Combine(repository, Wrappers.ShName), Wrappers.Sh(repository, unity, toolPath, repository));
            yield return (Path.Combine(repository, Wrappers.CmdName), Wrappers.Cmd(repository, unity, toolPath, repository));
        }

        /// <summary>chmod +x, so the block's ./entitas-graph runs; git keeps the bit for the team. Nothing to do on Windows.</summary>
        static bool MakeExecutable(string file)
        {
            if (OperatingSystem.IsWindows()) return true;
            try
            {
                var start = new ProcessStartInfo("chmod") { UseShellExecute = false, RedirectStandardError = true };
                start.ArgumentList.Add("+x");
                start.ArgumentList.Add(file);
                using var process = Process.Start(start);
                if (process == null) return false;
                process.WaitForExit();
                return process.ExitCode == 0;
            }
            catch (System.ComponentModel.Win32Exception)
            {
                return false;
            }
        }

        /// <summary>
        /// Instruction files whose entitas-graph block differs from what install would write now,
        /// and wrappers install wrote that differ from today's. The block lists the commands, so it
        /// goes stale when the package is updated, and nothing else would tell the user to run
        /// install again. Files without a block, and missing wrappers, are left out: install is opt-in. A tool running from outside the repository (a build of the framework
        /// itself) would write an absolute path, so it does not judge the project's block.
        /// </summary>
        public static List<string> OutdatedBlocks(string unityRoot, string toolPath)
        {
            var unity = Path.GetFullPath(unityRoot);
            var repository = FindGitRoot(unity) ?? unity;
            var outdated = new List<string>();
            if (!InstructionBlock.IsUnder(toolPath, repository)) return outdated;

            foreach (var file in DefaultTargets(repository))
            {
                var existing = TextFile.Read(file);
                if (existing == null) continue;
                try
                {
                    if (InstructionBlock.Uninstall(existing.Text) == null) continue;
                    var block = InstructionBlock.Build(Path.GetDirectoryName(file)!, unity, toolPath, repository);
                    if (InstructionBlock.Install(existing.Text, block) != existing.Text) outdated.Add(file);
                }
                catch (FormatException)
                {
                    outdated.Add(file);
                }
            }

            foreach (var (file, text) in WrapperTexts(repository, unity, toolPath))
            {
                var existing = TextFile.Read(file);
                if (existing != null && Wrappers.IsOurs(existing.Text) && existing.Text != text) outdated.Add(file);
            }

            return outdated;
        }

        /// <summary>
        /// The nearest folder at or above the Unity root holding <c>.git</c> — a folder, or a file
        /// in a worktree or submodule. Assistants run at the repository root, and in many projects
        /// that is above the Unity root.
        /// </summary>
        public static string? FindGitRoot(string unityRoot)
        {
            for (var dir = new DirectoryInfo(unityRoot); dir != null; dir = dir.Parent)
            {
                var git = Path.Combine(dir.FullName, ".git");
                if (Directory.Exists(git) || File.Exists(git)) return dir.FullName;
            }

            return null;
        }

        /// <summary>CLAUDE.md in the repository root, and AGENTS.md next to it when the project has one.</summary>
        public static List<string> DefaultTargets(string repositoryRoot)
        {
            var targets = new List<string> { Path.Combine(repositoryRoot, ClaudeFile) };
            var agents = Path.Combine(repositoryRoot, AgentsFile);
            if (File.Exists(agents)) targets.Add(agents);
            return targets;
        }

        static List<string> Targets(IReadOnlyList<string> targets, string repositoryRoot)
        {
            if (targets.Count == 0) return DefaultTargets(repositoryRoot);
            var files = targets.Select(Path.GetFullPath).Distinct().ToList();
            foreach (var file in files)
                if (Directory.Exists(file))
                    throw new SetupException($"--target {file} is a folder — name the instruction file, e.g. {Path.Combine(file, ClaudeFile)}.");
            return files;
        }

        static string RequireUnityRoot(string unityRoot)
        {
            var root = Path.GetFullPath(unityRoot);
            if (!Directory.Exists(Path.Combine(root, "Assets")))
                throw new SetupException($"{root} is not a Unity project root (it has no Assets folder) — run from the project root or pass -r <dir>.");
            return root;
        }

        static FileChange Ignore(string gitRoot, string unityRoot)
        {
            var path = Path.Combine(gitRoot, ".gitignore");
            var existing = TextFile.Read(path);
            var output = InstructionBlock.RelativePath(gitRoot, Path.Combine(unityRoot, "entitas-graph-out"));
            if (existing != null && GitIgnore.Ignores(existing.Text, output))
                return new FileChange(path, FileOutcome.Unchanged, $"already ignores {output}/");

            var change = Write(path, existing, GitIgnore.Append(existing?.Text, GitIgnore.OutputLine));
            return new FileChange(change.Path, change.Outcome, $"+ {GitIgnore.OutputLine}");
        }

        static T Apply<T>(string file, Func<T> update)
        {
            try
            {
                return update();
            }
            catch (FormatException ex)
            {
                throw new InvalidOperationException($"{file}: {ex.Message}");
            }
        }

        static FileChange Write(string file, TextFile? existing, string updated)
        {
            if (existing != null && existing.Text == updated) return new FileChange(file, FileOutcome.Unchanged);

            Directory.CreateDirectory(Path.GetDirectoryName(file)!);
            File.WriteAllText(file, updated, new UTF8Encoding(existing?.Bom ?? false));
            return new FileChange(file, existing == null ? FileOutcome.Created : FileOutcome.Updated);
        }

        /// <summary>A UTF-8 file and whether it starts with a BOM, so writing it back changes only what was meant to change.</summary>
        sealed class TextFile
        {
            public string Text { get; }
            public bool Bom { get; }

            TextFile(string text, bool bom)
            {
                Text = text;
                Bom = bom;
            }

            public static TextFile? Read(string path)
            {
                if (!File.Exists(path)) return null;
                var bytes = File.ReadAllBytes(path);
                var bom = bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF;
                return new TextFile(Encoding.UTF8.GetString(bytes, bom ? 3 : 0, bytes.Length - (bom ? 3 : 0)), bom);
            }
        }
    }
}
