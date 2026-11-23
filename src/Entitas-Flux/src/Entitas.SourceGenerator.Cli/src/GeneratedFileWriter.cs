using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using Entitas.SourceGenerator;
using Entitas.SourceGenerator.Discovery;
using Entitas.SourceGenerator.Rename;
using Microsoft.CodeAnalysis;

namespace Entitas.SourceGenerator.Cli
{
    /// <summary>
    /// Keeps an assembly's generated files in sync with its components
    /// (<c>Output = GenerationOutput.Files</c>). The code itself comes from the same
    /// <see cref="EntitasGenerators.Generate(GenerationInput)"/> the source generator runs;
    /// this only decides which files to write, leave alone or delete.
    ///
    /// Shared by entitas-gen and entitas-rename (linked, like <see cref="ProjectLoader"/>).
    ///
    /// The rules, each there for a reason:
    /// <list type="bullet">
    /// <item>A file is rewritten only when its content changed, ignoring line endings —
    /// Unity reimports and recompiles on every write, and a git checkout on Windows turns
    /// LF into CRLF without changing anything.</item>
    /// <item>Only files carrying the Entitas-Flux marker are ever overwritten or deleted. A
    /// file without it is someone's code, wherever it sits.</item>
    /// <item>A generated file that is no longer produced — the component was renamed or
    /// deleted, or OutputPath moved — is deleted with its .meta, wherever it is in the
    /// assembly; left behind, it would define its types twice.</item>
    /// <item>New files in a Unity project get a .meta whose GUID is derived from the path,
    /// so two people generating the same component on two branches produce the same .meta
    /// instead of a merge conflict.</item>
    /// <item>Nothing is written when a generator failed: its files would look orphaned and
    /// be deleted, leaving a hole in the API that only shows up as errors elsewhere.</item>
    /// <item>Nothing is written either when the files about to be deleted reveal a component
    /// renamed in the IDE (<see cref="RenameDetector"/>) whose old generated API is still in
    /// use, until someone decides (<see cref="WriterOptions.Renames"/>): deleting them is
    /// what breaks those usages, and what makes the old names impossible to resolve.</item>
    /// </list>
    /// </summary>
    public static class GeneratedFileWriter
    {
        static readonly UTF8Encoding Utf8NoBom = new UTF8Encoding(false);

        public static AssemblyResult Run(Compilation compilation, string projectRoot, WriterOptions? options = null)
        {
            options ??= new WriterOptions();
            projectRoot = Path.GetFullPath(projectRoot);

            var result = new AssemblyResult(compilation.AssemblyName ?? string.Empty);
            var config = GenerationConfig.From(compilation);
            if (!config.Enabled)
            {
                result.Mode = GenerationMode.NotConfigured;
                if (options.OutputDirectoryOverride != null)
                    result.Errors.Add(
                        "nothing to generate: the assembly must reference Entitas (Entitas.IComponent) AND declare " +
                        "at least one [assembly: ContextDefinition(\"...\")].");
                return result;
            }

            var stopwatch = System.Diagnostics.Stopwatch.StartNew();
            var input = GenerationInput.From(compilation, parallel: true);
            result.Timings.Add(("discovery", stopwatch.ElapsedMilliseconds));
            if (input.DiscoveryError != null)
            {
                result.Errors.Add(EntitasDiagnostics.DiscoveryFailed(input.DiscoveryError).GetMessage());
                return result;
            }

            result.ComponentFiles.AddRange(ComponentFiles(compilation, projectRoot));

            var filesMode = options.OutputDirectoryOverride != null || config.Options.Output == GenerationOutput.Files;
            result.Mode = filesMode ? GenerationMode.Files : GenerationMode.SourceGenerator;

            string? outputDirectory = null;
            if (filesMode)
            {
                outputDirectory = options.OutputDirectoryOverride != null
                    ? Path.GetFullPath(options.OutputDirectoryOverride)
                    : ResolveOutputDirectory(compilation, config.Options, projectRoot, result);
                if (outputDirectory == null)
                    return result;

                result.OutputDirectory = outputDirectory;
            }

            var sources = Array.Empty<GeneratedSource>() as IReadOnlyList<GeneratedSource>;
            if (filesMode)
            {
                stopwatch.Restart();
                var generation = EntitasGenerators.Generate(input);
                result.Timings.Add(("generation", stopwatch.ElapsedMilliseconds));
                foreach (var diagnostic in generation.Diagnostics)
                {
                    if (diagnostic.Severity == DiagnosticSeverity.Error)
                        result.Errors.Add($"{diagnostic.Id}: {diagnostic.GetMessage()}");
                    else
                        result.Warnings.Add($"{diagnostic.Id}: {diagnostic.GetMessage()}");
                }

                if (result.Errors.Count > 0)
                    return result;

                sources = generation.Sources;
            }

            stopwatch.Restart();
            var existing = ExistingFiles(compilation, outputDirectory);
            var desired = new SortedDictionary<string, string>(PathComparer);
            foreach (var source in sources)
                desired[Combine(outputDirectory!, source.FileName)] = GeneratedFiles.FileText(source);

            if (options.Check)
            {
                result.Staleness = GeneratedFiles.Compare(
                    sources,
                    existing.Select(path => new ExistingGeneratedFile(
                            path,
                            outputDirectory == null ? null : GeneratedFiles.LogicalName(path, outputDirectory),
                            GeneratedFiles.Hash(File.ReadAllText(path))))
                        .ToArray());
                return result;
            }

            if (filesMode && existing.Any(path => !desired.ContainsKey(path)))
            {
                stopwatch.Stop();
                if (!HandleRename(compilation, input, sources, outputDirectory!, existing, options, result))
                    return result;
                stopwatch.Start();
            }

            var isUnityProject = Directory.Exists(Path.Combine(projectRoot, "ProjectSettings"));

            // Reading what is there is most of a no-change run; do it on every core.
            var current = desired.Keys
                .AsParallel()
                .Select(path => (Path: path, Text: File.Exists(path) ? File.ReadAllText(path) : null))
                .ToDictionary(entry => entry.Path, entry => entry.Text, PathComparer);

            foreach (var pair in desired)
            {
                var path = pair.Key;
                var text = pair.Value;

                if (current[path] is { } existingText)
                {
                    if (GeneratedFiles.NormalizeNewlines(existingText) == text)
                    {
                        result.Unchanged++;
                        continue;
                    }

                    if (!GeneratedFiles.IsGenerated(existingText))
                    {
                        result.Errors.Add(
                            $"{Relative(path, projectRoot)} exists and was not written by Entitas-Flux — " +
                            "move it out of the output folder, it is left untouched.");
                        continue;
                    }
                }
                else
                {
                    EnsureDirectory(Path.GetDirectoryName(path)!, projectRoot, isUnityProject);
                    if (isUnityProject && IsUnityAsset(path, projectRoot) && !File.Exists(path + ".meta"))
                        File.WriteAllText(path + ".meta", ScriptMeta(Guid(path, projectRoot)), Utf8NoBom);
                }

                File.WriteAllText(path, text, Utf8NoBom);
                result.Written.Add(path);
            }

            foreach (var path in existing.Where(path => !desired.ContainsKey(path)).OrderBy(p => p, PathComparer))
            {
                File.Delete(path);
                if (File.Exists(path + ".meta"))
                    File.Delete(path + ".meta");
                result.Deleted.Add(path);
            }

            foreach (var directory in result.Deleted.Select(Path.GetDirectoryName).Distinct(PathComparer).ToList())
            {
                if (directory != null)
                    RemoveEmptyDirectories(directory, projectRoot);
            }

            result.Timings.Add(("sync", stopwatch.ElapsedMilliseconds));

            return result;
        }

        // -- IDE renames ----------------------------------------------------------

        /// <summary>
        /// Generation is about to delete files: check they are not the API of a component
        /// renamed in the IDE that code still uses. False when nothing may be written.
        /// </summary>
        static bool HandleRename(
            Compilation compilation,
            GenerationInput input,
            IReadOnlyList<GeneratedSource> sources,
            string outputDirectory,
            IReadOnlyCollection<string> existing,
            WriterOptions options,
            AssemblyResult result)
        {
            var stopwatch = System.Diagnostics.Stopwatch.StartNew();
            var detection = RenameDetector.Detect(compilation, input, sources, outputDirectory, existing);
            if (detection.Warning != null)
                result.Warnings.Add(detection.Warning);

            if (detection.Rename is not { } rename)
            {
                result.Timings.Add(("rename check", stopwatch.ElapsedMilliseconds));
                return true;
            }

            result.Rename = rename;
            if (options.ReferencingEdits != null)
                rename.External.AddRange(options.ReferencingEdits(compilation, rename.Plan)
                    .Where(edits => edits.Files.Count > 0 || edits.Warnings.Count > 0));
            result.Timings.Add(("rename detection", stopwatch.ElapsedMilliseconds));

            if (rename.UsageCount == 0)
            {
                rename.Outcome = RenameOutcome.NothingToUpdate;
                return true;
            }

            var handling = options.ApplyRenames?.Contains(rename.Key) == true ? RenameHandling.Apply
                : options.IgnoreRenames?.Contains(rename.Key) == true ? RenameHandling.Ignore
                : options.Renames;

            if (handling == RenameHandling.Apply)
            {
                try
                {
                    rename.EditedFiles.AddRange(RenameDetector.Apply(rename));
                }
                catch (Exception exception) when (exception is RenameException || exception is IOException ||
                                                  exception is UnauthorizedAccessException)
                {
                    result.Errors.Add(
                        $"could not update the usages of {rename.OldClassName}'s generated API: {exception.Message} " +
                        "Nothing was written.");
                    return false;
                }

                rename.Outcome = RenameOutcome.Applied;
                return true;
            }

            if (handling == RenameHandling.Ignore)
            {
                rename.Outcome = RenameOutcome.Ignored;
                return true;
            }

            rename.Outcome = RenameOutcome.Pending;
            return false;
        }

        // -- output folder ------------------------------------------------------

        static string? ResolveOutputDirectory(
            Compilation compilation,
            EntitasGenerationOptions options,
            string projectRoot,
            AssemblyResult result)
        {
            var configured = GeneratedFiles.OutputDirectory(compilation, options, projectRoot);
            if (configured == null)
            {
                result.Errors.Add("cannot work out where to write: set OutputPath on [assembly: EntitasGeneration].");
                return null;
            }

            var full = Path.GetFullPath(configured);
            if (!IsInside(full, projectRoot) || PathEquals(full, projectRoot))
            {
                result.Errors.Add(
                    $"OutputPath '{options.OutputPath}' resolves to {full}, which is not inside the project ({projectRoot}).");
                return null;
            }

            return full;
        }

        /// <summary>
        /// Generated files already present: those the compilation contains (wherever they
        /// are — a moved OutputPath leaves its old files behind) plus those on disk in the
        /// output folder that the project file does not list yet.
        /// </summary>
        static List<string> ExistingFiles(Compilation compilation, string? outputDirectory)
        {
            var paths = new HashSet<string>(
                GeneratedFiles.FindGeneratedTrees(compilation)
                    .Select(tree => tree.FilePath)
                    .Where(path => !string.IsNullOrEmpty(path) && File.Exists(path))
                    .Select(Path.GetFullPath),
                PathComparer);

            if (outputDirectory != null && Directory.Exists(outputDirectory))
            {
                foreach (var path in Directory.EnumerateFiles(outputDirectory, "*.cs", SearchOption.AllDirectories))
                {
                    if (!paths.Contains(path) && GeneratedFiles.IsGenerated(ReadHead(path)))
                        paths.Add(Path.GetFullPath(path));
                }
            }

            return paths.ToList();
        }

        /// <summary>
        /// Source files whose edits can change the generated code: every declaration of a
        /// component or attributed type, and the files carrying the assembly-level config.
        /// The Unity editor regenerates automatically only when one of these (or a file
        /// that might become one) changes.
        /// </summary>
        static IEnumerable<string> ComponentFiles(Compilation compilation, string projectRoot)
        {
            var files = new HashSet<string>(StringComparer.Ordinal);

            foreach (var type in EntitasDiscovery.GetRelevantTypes(compilation, parallel: true))
            {
                foreach (var reference in type.DeclaringSyntaxReferences)
                    Add(reference.SyntaxTree.FilePath);
            }

            foreach (var attribute in compilation.Assembly.GetAttributes())
            {
                var name = attribute.AttributeClass?.Name;
                if (name == "ContextDefinitionAttribute" || name == "EntitasGenerationAttribute" ||
                    name == "DisableEntitasGeneratorAttribute")
                    Add(attribute.ApplicationSyntaxReference?.SyntaxTree.FilePath);
            }

            return files.OrderBy(f => f, StringComparer.Ordinal);

            void Add(string? path)
            {
                if (!string.IsNullOrEmpty(path))
                    files.Add(Relative(Path.GetFullPath(path!), projectRoot));
            }
        }

        // -- filesystem ---------------------------------------------------------

        static void EnsureDirectory(string directory, string projectRoot, bool isUnityProject)
        {
            if (Directory.Exists(directory))
                return;

            var parent = Path.GetDirectoryName(directory);
            if (parent != null)
                EnsureDirectory(parent, projectRoot, isUnityProject);

            Directory.CreateDirectory(directory);

            if (isUnityProject && IsUnityAsset(directory, projectRoot) && !File.Exists(directory + ".meta"))
                File.WriteAllText(directory + ".meta", FolderMeta(Guid(directory, projectRoot)), Utf8NoBom);
        }

        /// <summary>
        /// Walks up from a folder a generated file was deleted from, removing each folder
        /// (and its .meta) that is left empty — orphaned .meta files of vanished assets do
        /// not count as content. Never removes the project root, Assets or Packages, and
        /// stops at the first folder holding anything else.
        /// </summary>
        static void RemoveEmptyDirectories(string directory, string projectRoot)
        {
            var guarded = new[]
            {
                projectRoot,
                Path.Combine(projectRoot, "Assets"),
                Path.Combine(projectRoot, "Packages")
            };

            var current = directory;
            while (current != null && IsInside(current, projectRoot) && !guarded.Any(g => PathEquals(current, g)))
            {
                if (Directory.Exists(current))
                {
                    if (Directory.EnumerateDirectories(current).Any())
                        return;

                    var files = Directory.EnumerateFiles(current).ToArray();
                    if (files.Any(file => !IsOrphanedMeta(file)))
                        return;

                    foreach (var orphan in files)
                        File.Delete(orphan);

                    Directory.Delete(current);
                    if (File.Exists(current + ".meta"))
                        File.Delete(current + ".meta");
                }

                current = Path.GetDirectoryName(current);
            }
        }

        static bool IsOrphanedMeta(string file)
        {
            if (!file.EndsWith(".meta", StringComparison.OrdinalIgnoreCase))
                return false;

            var asset = file.Substring(0, file.Length - ".meta".Length);
            return !File.Exists(asset) && !Directory.Exists(asset);
        }

        static string ReadHead(string path)
        {
            using var reader = new StreamReader(path, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
            var buffer = new char[256];
            var read = reader.Read(buffer, 0, buffer.Length);
            return new string(buffer, 0, read);
        }

        internal static string Combine(string directory, string fileName) =>
            Path.GetFullPath(Path.Combine(directory, fileName.Replace('/', Path.DirectorySeparatorChar)));

        static bool IsUnityAsset(string path, string projectRoot) =>
            IsInside(path, Path.Combine(projectRoot, "Assets")) || IsInside(path, Path.Combine(projectRoot, "Packages"));

        internal static bool IsInside(string path, string directory)
        {
            var full = Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar);
            var root = Path.GetFullPath(directory).TrimEnd(Path.DirectorySeparatorChar);
            return PathEquals(full, root) || full.StartsWith(root + Path.DirectorySeparatorChar, PathComparison);
        }

        static bool PathEquals(string left, string right) =>
            string.Equals(
                Path.GetFullPath(left).TrimEnd(Path.DirectorySeparatorChar),
                Path.GetFullPath(right).TrimEnd(Path.DirectorySeparatorChar),
                PathComparison);

        static StringComparison PathComparison =>
            OperatingSystem.IsLinux() ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;

        internal static StringComparer PathComparer =>
            OperatingSystem.IsLinux() ? StringComparer.Ordinal : StringComparer.OrdinalIgnoreCase;

        public static string Relative(string path, string root)
        {
            var relative = Path.GetRelativePath(root, path);
            return (relative.StartsWith("..", StringComparison.Ordinal) ? path : relative).Replace('\\', '/');
        }

        // -- Unity .meta ----------------------------------------------------------

        /// <summary>Stable per project-relative path, like the package's own .meta files.</summary>
        static string Guid(string path, string projectRoot)
        {
            using var md5 = MD5.Create();
            var bytes = md5.ComputeHash(Encoding.UTF8.GetBytes("entitas-flux-generated/" + Relative(path, projectRoot)));
            return string.Concat(bytes.Select(b => b.ToString("x2")));
        }

        static string ScriptMeta(string guid) =>
            "fileFormatVersion: 2\n" +
            $"guid: {guid}\n" +
            "MonoImporter:\n" +
            "  externalObjects: {}\n" +
            "  serializedVersion: 2\n" +
            "  defaultReferences: []\n" +
            "  executionOrder: 0\n" +
            "  icon: {instanceID: 0}\n" +
            "  userData: \n" +
            "  assetBundleName: \n" +
            "  assetBundleVariant: \n";

        static string FolderMeta(string guid) =>
            "fileFormatVersion: 2\n" +
            $"guid: {guid}\n" +
            "folderAsset: yes\n" +
            "DefaultImporter:\n" +
            "  externalObjects: {}\n" +
            "  userData: \n" +
            "  assetBundleName: \n" +
            "  assetBundleVariant: \n";
    }

    public sealed class WriterOptions
    {
        /// <summary>Report what would change instead of changing it.</summary>
        public bool Check;

        /// <summary>
        /// Write here regardless of the assembly's Output setting — the pre-file-mode
        /// <c>entitas-gen &lt;csproj&gt; &lt;dir&gt;</c> form.
        /// </summary>
        public string? OutputDirectoryOverride;

        /// <summary>
        /// What to do when the files about to be deleted reveal a component renamed in the IDE
        /// whose old generated API is still used. <see cref="RenameHandling.Ask"/> (the default)
        /// reports it in <see cref="AssemblyResult.Rename"/> and writes nothing.
        /// </summary>
        public RenameHandling Renames = RenameHandling.Ask;

        /// <summary>
        /// Renames (<see cref="DetectedRename.Key"/>) someone was shown and said yes to: applied
        /// whatever <see cref="Renames"/> says. A different rename found instead is not.
        /// </summary>
        public ICollection<string>? ApplyRenames;

        /// <summary>Renames someone chose to regenerate without updating the usages of.</summary>
        public ICollection<string>? IgnoreRenames;

        /// <summary>
        /// Finds the usages in assemblies that reference the one being generated (Editor
        /// assemblies, test assemblies) — supplied by the host, which knows the project.
        /// </summary>
        public Func<Compilation, RenamePlan, IReadOnlyList<ExternalEdits>>? ReferencingEdits;

        internal WriterOptions With(Func<Compilation, RenamePlan, IReadOnlyList<ExternalEdits>> referencingEdits) =>
            new WriterOptions
            {
                Check = Check,
                OutputDirectoryOverride = OutputDirectoryOverride,
                Renames = Renames,
                ApplyRenames = ApplyRenames,
                IgnoreRenames = IgnoreRenames,
                ReferencingEdits = ReferencingEdits ?? referencingEdits
            };
    }

    public enum GenerationMode { NotConfigured, SourceGenerator, Files }

    public sealed class AssemblyResult
    {
        public string AssemblyName { get; }
        public GenerationMode Mode { get; set; } = GenerationMode.NotConfigured;
        public string? OutputDirectory { get; set; }
        public List<string> Written { get; } = new List<string>();
        public List<string> Deleted { get; } = new List<string>();
        public int Unchanged { get; set; }
        public List<string> Errors { get; } = new List<string>();
        public List<string> Warnings { get; } = new List<string>();
        public List<string> ComponentFiles { get; } = new List<string>();

        /// <summary>Set in check mode.</summary>
        public Staleness? Staleness { get; set; }

        /// <summary>A component renamed in the IDE, found in the files this run was about to delete.</summary>
        public DetectedRename? Rename { get; set; }

        /// <summary>Where the time went, for --verbose.</summary>
        public List<(string Phase, long Milliseconds)> Timings { get; } = new List<(string, long)>();

        public bool Changed => Written.Count > 0 || Deleted.Count > 0 || Rename?.EditedFiles.Count > 0;

        public AssemblyResult(string assemblyName)
        {
            AssemblyName = assemblyName;
        }
    }
}
