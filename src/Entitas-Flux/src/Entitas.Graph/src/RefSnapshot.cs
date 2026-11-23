using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using Entitas.SourceGenerator.Cli;

namespace Entitas.Graph
{
    /// <summary>
    /// The Unity project's C# sources as they are at a git ref, extracted into a temporary
    /// directory, plus the project to compile them with — so <c>diff --ref</c> can build the
    /// old graph without touching the user's checkout: no worktree, no checkout, no stash, and
    /// nothing written into the project. Disposing deletes the directory.
    ///
    /// Only the sources come from the ref. References (Unity's DLLs, Library/ScriptAssemblies
    /// for asmdef dependencies) and defines are the current csproj's, read where they are —
    /// Library/ is not versioned, so there is no other copy of them. A package or asmdef
    /// reference added or removed since the ref is therefore compiled against as it is today.
    /// That holds for both sides of <c>diff --ref A --ref B</c> as well: each snapshot maps
    /// today's csproj onto its own commit, and both compile with today's references and defines.
    /// </summary>
    public sealed class RefSnapshot : IDisposable
    {
        readonly TempDirectory _directory;

        public string Ref { get; }
        public string Commit { get; }

        /// <summary>The Unity root inside the snapshot; graph paths are relative to it, like the real root's.</summary>
        public string Root { get; }

        public ProjectLoader.ProjectInfo Project { get; }

        /// <summary>Files the ref has that the current csproj does not list, added by the folder heuristic.</summary>
        public int AddedFiles { get; }

        /// <summary>Files the current csproj lists that the ref does not have.</summary>
        public int DroppedFiles { get; }

        RefSnapshot(TempDirectory directory, string gitRef, string commit, string root, ProjectLoader.ProjectInfo project, int added, int dropped)
        {
            _directory = directory;
            Ref = gitRef;
            Commit = commit;
            Root = root;
            Project = project;
            AddedFiles = added;
            DroppedFiles = dropped;
        }

        /// <summary>
        /// The commit <paramref name="gitRef"/> names and its parents, first parent first (none
        /// for a root commit) — what <c>diff --commit</c> needs to pick the old side.
        /// </summary>
        public static (string Commit, IReadOnlyList<string> Parents) Parents(string unityRoot, string gitRef)
        {
            unityRoot = Path.GetFullPath(unityRoot);
            var commit = ResolveCommit(unityRoot, gitRef);
            // "<commit> <first parent> <second parent>…" on one line.
            var ids = Git(unityRoot, "rev-list", "--parents", "-n", "1", commit)
                .Split(new[] { ' ', '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries);
            return (commit, ids.Skip(1).ToList());
        }

        static string ResolveCommit(string unityRoot, string gitRef)
        {
            var commit = Git(unityRoot, "rev-parse", "--verify", "--quiet", gitRef + "^{commit}").Trim();
            if (commit.Length == 0)
                throw new InvalidOperationException($"'{gitRef}' is not a commit in the repository of {unityRoot}");
            return commit;
        }

        public static RefSnapshot Extract(string unityRoot, ProjectLoader.ProjectInfo current, string gitRef)
        {
            unityRoot = Path.GetFullPath(unityRoot);
            // The repository and the Unity root's path inside it ("src/game/", or "" at the top).
            // archive and ls-tree run from the top: from a subfolder they would look for that
            // subfolder again inside the commit:prefix tree.
            var location = Git(unityRoot, "rev-parse", "--show-toplevel", "--show-prefix").Split('\n');
            var repository = location[0].Trim();
            var prefix = location.Length > 1 ? location[1].Trim() : string.Empty;
            var commit = ResolveCommit(unityRoot, gitRef);

            var directory = new TempDirectory(Path.Combine(Path.GetTempPath(), $"entitas-graph-{commit.Substring(0, Math.Min(10, commit.Length))}-{Path.GetRandomFileName()}"));
            try
            {
                var root = Path.Combine(directory.Location, "project");
                ExtractSources(repository, commit, prefix, root);

                var atRef = Directory.EnumerateFiles(root, "*.cs", SearchOption.AllDirectories)
                    .Select(path => Relative(root, path))
                    .ToList();
                // Separately: a pathspec that matches nothing fails git archive, and many projects have no asmdef.
                var assemblyFolders = Git(repository, "ls-tree", "-r", "-z", "--name-only", $"{commit}:{prefix}")
                    .Split('\0')
                    .Where(path => path.EndsWith(".asmdef", StringComparison.OrdinalIgnoreCase) || path.EndsWith(".asmref", StringComparison.OrdinalIgnoreCase))
                    .Select(Folder);

                // Files outside the Unity root (a local package) and under Library/ (package
                // caches) are not versioned with it: they are the same for both sides, so they
                // stay where they are.
                var listed = current.SourceFiles.Concat(current.MissingSourceFiles).Select(path => Relative(unityRoot, path)).ToList();
                var unversioned = listed.Where(IsUnversioned).ToList();
                var mapped = MapSources(listed.Where(path => !IsUnversioned(path)), atRef, assemblyFolders);

                var listedSet = new HashSet<string>(listed, GeneratedFileWriter.PathComparer);
                var mappedSet = new HashSet<string>(mapped, GeneratedFileWriter.PathComparer);
                var project = new ProjectLoader.ProjectInfo(
                    mapped.Select(path => Path.GetFullPath(Path.Combine(root, path)))
                        .Concat(unversioned.Select(path => Path.GetFullPath(Path.Combine(unityRoot, path))).Where(File.Exists))
                        .ToList(),
                    Array.Empty<string>(),
                    current.References,
                    current.Defines,
                    current.LanguageVersion);

                return new RefSnapshot(directory, gitRef, commit, root, project,
                    added: mapped.Count(path => !listedSet.Contains(path)),
                    dropped: listed.Count(path => !IsUnversioned(path) && !mappedSet.Contains(path)));
            }
            catch
            {
                directory.Dispose();
                throw;
            }
        }

        /// <summary>
        /// Which of the ref's files to compile, as paths relative to the Unity root with forward
        /// slashes. Unity lists in the csproj the files of the CURRENT checkout, so it is mapped:
        ///
        /// - a listed file the ref has is kept; one it lacks (added since, or untracked) is dropped;
        /// - a file only the ref has (deleted or renamed since) is added when it would belong to
        ///   the same assembly: walking up from its folder, the first folder that holds listed files
        ///   is reached before a folder with an .asmdef/.asmref (another assembly) or one named
        ///   Editor (Unity's editor assembly). In practice: a file next to listed files, or in a
        ///   subfolder of theirs that was deleted since.
        ///
        /// It misses what Unity would place by rules it does not model — the firstpass Plugins
        /// folders, an asmdef moved since the ref — and a file in a folder tree of the ref that
        /// holds none of the listed files.
        /// </summary>
        public static IReadOnlyList<string> MapSources(IEnumerable<string> listed, IEnumerable<string> atRef, IEnumerable<string> assemblyFolders)
        {
            var listedSet = new HashSet<string>(listed, GeneratedFileWriter.PathComparer);
            var listedFolders = new HashSet<string>(listedSet.Select(Folder), GeneratedFileWriter.PathComparer);
            var otherAssemblies = new HashSet<string>(assemblyFolders, GeneratedFileWriter.PathComparer);
            var refFiles = atRef.Distinct(GeneratedFileWriter.PathComparer).ToList();

            return refFiles.Where(path => listedSet.Contains(path) || SameAssembly(path))
                .OrderBy(path => path, StringComparer.Ordinal)
                .ToList();

            bool SameAssembly(string path)
            {
                for (var folder = Folder(path); ; folder = Folder(folder))
                {
                    if (listedFolders.Contains(folder)) return true;
                    if (folder.Length == 0 || otherAssemblies.Contains(folder) ||
                        string.Equals(Name(folder), "Editor", StringComparison.OrdinalIgnoreCase))
                        return false;
                }
            }
        }

        /// <summary>
        /// <c>git archive</c> of the Unity root's .cs files at the commit — the assets would be
        /// gigabytes — as an uncompressed zip read in memory and extracted here, so no tar binary
        /// is needed. The tree-ish <c>commit:prefix</c> takes the prefix literally (a glob character
        /// in a folder name stays a character) and strips it from the archived paths.
        /// </summary>
        static void ExtractSources(string repository, string commit, string prefix, string destination)
        {
            using var zip = new MemoryStream();
            var info = new ProcessStartInfo("git")
            {
                WorkingDirectory = repository, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false
            };
            foreach (var argument in new[] { "archive", "--format=zip", "-0", $"{commit}:{prefix}", "--", "*.cs" })
                info.ArgumentList.Add(argument);

            using (var process = StartGit(info))
            {
                var error = process.StandardError.ReadToEndAsync();
                process.StandardOutput.BaseStream.CopyTo(zip);
                process.WaitForExit();
                if (process.ExitCode != 0)
                    throw new InvalidOperationException($"git archive failed: {error.Result.Trim()}");
            }

            zip.Position = 0;
            using var archive = new ZipArchive(zip, ZipArchiveMode.Read);
            archive.ExtractToDirectory(destination);
        }

        static string Git(string workingDirectory, params string[] arguments)
        {
            var info = new ProcessStartInfo("git")
            {
                WorkingDirectory = workingDirectory, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false
            };
            foreach (var argument in arguments)
                info.ArgumentList.Add(argument);

            using var process = StartGit(info);
            var error = process.StandardError.ReadToEndAsync();
            var output = process.StandardOutput.ReadToEnd();
            process.WaitForExit();
            if (process.ExitCode != 0 && !arguments.Contains("--quiet"))
                throw new InvalidOperationException($"git {arguments[0]} failed in {workingDirectory}: {error.Result.Trim()}");
            return process.ExitCode == 0 ? output : string.Empty;
        }

        static Process StartGit(ProcessStartInfo info)
        {
            try
            {
                return Process.Start(info) ?? throw new InvalidOperationException("could not start git");
            }
            catch (Win32Exception)
            {
                throw new InvalidOperationException("--ref needs git on the PATH");
            }
        }

        static bool IsUnversioned(string relative) =>
            relative.StartsWith("../", StringComparison.Ordinal) || Path.IsPathRooted(relative) ||
            relative.StartsWith("Library/", StringComparison.OrdinalIgnoreCase);

        static string Relative(string root, string path) => Path.GetRelativePath(root, path).Replace('\\', '/');

        static string Folder(string path)
        {
            var slash = path.LastIndexOf('/');
            return slash < 0 ? string.Empty : path.Substring(0, slash);
        }

        static string Name(string folder) => folder.Substring(folder.LastIndexOf('/') + 1);

        public void Dispose() => _directory.Dispose();

        /// <summary>
        /// A temporary directory deleted on Dispose and on Ctrl+C, which ends the process
        /// without running finally blocks — a copy of every source would be left behind. The
        /// handler is registered before anything is extracted, since extracting is a good part
        /// of the time a snapshot takes. With two snapshots (<c>--ref A --ref B</c>) each
        /// registers its own handler, and Ctrl+C runs them all.
        /// </summary>
        sealed class TempDirectory : IDisposable
        {
            public string Location { get; }

            public TempDirectory(string location)
            {
                Location = location;
                Console.CancelKeyPress += OnCancel;
                Directory.CreateDirectory(location);
            }

            // The build may still be writing into the directory on another thread; a second
            // attempt catches a file created between the listing and the delete.
            void OnCancel(object? sender, ConsoleCancelEventArgs e)
            {
                if (!Delete(Location)) Delete(Location);
            }

            public void Dispose()
            {
                Console.CancelKeyPress -= OnCancel;
                Delete(Location);
            }

            static bool Delete(string directory)
            {
                try
                {
                    if (Directory.Exists(directory))
                        Directory.Delete(directory, recursive: true);
                    return true;
                }
                catch (IOException)
                {
                    // A temp directory left behind is not worth failing the answer for.
                    return false;
                }
                catch (UnauthorizedAccessException)
                {
                    return false;
                }
            }
        }
    }
}
