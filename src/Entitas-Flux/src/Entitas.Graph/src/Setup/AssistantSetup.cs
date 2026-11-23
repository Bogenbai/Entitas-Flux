using System;
using System.Collections.Generic;
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

            foreach (var (file, existing, updated) in plan)
                result.Files.Add(Write(file, existing, updated));

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

            return result;
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
