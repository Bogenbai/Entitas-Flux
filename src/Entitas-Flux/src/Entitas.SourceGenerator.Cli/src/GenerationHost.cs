using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;

namespace Entitas.SourceGenerator.Cli
{
    /// <summary>
    /// Finds the assemblies that generate, builds their compilations and runs
    /// <see cref="GeneratedFileWriter"/> over them — for a terminal (from the csproj files an
    /// IDE package writes) or for the Unity editor (from a <see cref="GenerationRequest"/>).
    /// Shared with entitas-rename, which regenerates after a rename in file mode.
    /// </summary>
    public static class GenerationHost
    {
        /// <summary>
        /// The csproj files in a Unity project root that declare contexts. Unity's IDE
        /// packages write one per assembly, plus a <c>.Player.csproj</c> twin with player
        /// defines that must be skipped — it is the same assembly. A cheap textual filter
        /// (references Entitas, mentions ContextDefinition) keeps package assemblies from
        /// being parsed at all.
        /// </summary>
        public static IReadOnlyList<string> FindGeneratingProjects(string root)
        {
            var found = new List<string>();
            foreach (var csproj in Directory.EnumerateFiles(root, "*.csproj", SearchOption.TopDirectoryOnly)
                         .Where(p => !p.EndsWith(".Player.csproj", StringComparison.OrdinalIgnoreCase))
                         .OrderBy(p => p, StringComparer.Ordinal))
            {
                var xml = File.ReadAllText(csproj);
                if (xml.IndexOf("Entitas.dll", StringComparison.OrdinalIgnoreCase) < 0 &&
                    xml.IndexOf("Entitas.csproj", StringComparison.OrdinalIgnoreCase) < 0)
                    continue;

                var project = ProjectLoader.ParseProject(csproj);
                if (MentionsContextDefinition(project.SourceFiles))
                    found.Add(csproj);
            }

            return found;
        }

        public static AssemblyResult RunProject(string csprojPath, string projectRoot, WriterOptions options)
        {
            var stopwatch = Stopwatch.StartNew();
            var project = ProjectLoader.ParseProject(csprojPath);
            var compilation = ProjectLoader.BuildCompilation(csprojPath, project);
            var parse = stopwatch.ElapsedMilliseconds;

            var result = GeneratedFileWriter.Run(compilation, projectRoot, options);
            result.Timings.Insert(0, ($"parse {project.SourceFiles.Count} files", parse));
            return result;
        }

        public static GenerationResponse RunRequest(GenerationRequest request)
        {
            var stopwatch = Stopwatch.StartNew();
            var response = new GenerationResponse();

            if (string.IsNullOrEmpty(request.projectRoot) || !Directory.Exists(request.projectRoot))
            {
                response.error = $"project root not found: '{request.projectRoot}'";
                return response;
            }

            var root = Path.GetFullPath(request.projectRoot);
            foreach (var assembly in request.assemblies)
            {
                var sources = assembly.sourceFiles
                    .Select(f => Path.GetFullPath(Path.IsPathRooted(f) ? f : Path.Combine(root, f)))
                    .Where(File.Exists)
                    .ToList();

                if (!assembly.references.Any(r => string.Equals(Path.GetFileName(r), "Entitas.dll", StringComparison.OrdinalIgnoreCase)) ||
                    !MentionsContextDefinition(sources))
                    continue;

                var project = new ProjectLoader.ProjectInfo(
                    sources,
                    new string[0],
                    assembly.references.Where(File.Exists).Distinct(StringComparer.Ordinal).ToList(),
                    assembly.defines,
                    assembly.languageVersion);

                var compilation = ProjectLoader.Compile(assembly.name, project);
                var result = GeneratedFileWriter.Run(compilation, root, new WriterOptions { Check = request.check });
                response.assemblies.Add(AssemblyReport.From(result, root));
            }

            response.milliseconds = stopwatch.ElapsedMilliseconds;
            return response;
        }

        /// <summary>
        /// The project plus every generated file in <paramref name="outputDirectory"/> it does
        /// not list yet — Unity's IDE packages rewrite the csproj only when Unity notices new
        /// files, so right after a generation it is usually behind.
        /// </summary>
        public static ProjectLoader.ProjectInfo WithGeneratedFiles(ProjectLoader.ProjectInfo project, string outputDirectory)
        {
            if (!Directory.Exists(outputDirectory))
                return project;

            var listed = new HashSet<string>(project.SourceFiles, GeneratedFileWriter.PathComparer);
            var missing = Directory.EnumerateFiles(outputDirectory, "*.cs", SearchOption.AllDirectories)
                .Select(Path.GetFullPath)
                .Where(path => !listed.Contains(path) && GeneratedFiles.IsGenerated(File.ReadAllText(path)))
                .OrderBy(path => path, StringComparer.Ordinal)
                .ToList();

            if (missing.Count == 0)
                return project;

            return new ProjectLoader.ProjectInfo(
                project.SourceFiles.Concat(missing).ToList(),
                project.MissingSourceFiles,
                project.References,
                project.Defines,
                project.LanguageVersion);
        }

        static bool MentionsContextDefinition(IEnumerable<string> files) =>
            files.AsParallel().Any(file =>
                File.Exists(file) && File.ReadAllText(file).IndexOf("ContextDefinition", StringComparison.Ordinal) >= 0);

        /// <summary>One line per assembly, plus the changed files when asked.</summary>
        public static void Print(AssemblyResult result, string root, bool verbose, TextWriter output)
        {
            foreach (var warning in result.Warnings)
                output.WriteLine($"  warning: {warning}");
            foreach (var error in result.Errors)
                output.WriteLine($"  error: {error}");

            switch (result.Mode)
            {
                case GenerationMode.NotConfigured:
                    output.WriteLine(
                        $"{result.AssemblyName}: no [assembly: ContextDefinition(\"…\")] resolves — nothing to generate. " +
                        "If it is declared, check the assembly references Entitas.dll and Entitas.CodeGeneration.Attributes.dll.");
                    return;

                case GenerationMode.SourceGenerator when result.Staleness != null:
                    output.WriteLine(result.Staleness.IsUpToDate
                        ? $"{result.AssemblyName}: generates with the source generator; no generated files on disk."
                        : $"{result.AssemblyName}: generates with the source generator, but {result.Staleness.Orphans.Count} generated files are left on disk.");
                    return;

                case GenerationMode.SourceGenerator:
                    output.WriteLine(result.Deleted.Count == 0
                        ? $"{result.AssemblyName}: generates with the source generator " +
                          "(set [assembly: EntitasGeneration(Output = GenerationOutput.Files)] to write files) — nothing to do."
                        : $"{result.AssemblyName}: generates with the source generator — deleted {result.Deleted.Count} leftover generated files.");
                    break;

                case GenerationMode.Files when result.Staleness != null:
                    var folder = GeneratedFileWriter.Relative(result.OutputDirectory!, root);
                    output.WriteLine(result.Staleness.IsUpToDate
                        ? $"{result.AssemblyName} → {folder}: up to date."
                        : $"{result.AssemblyName} → {folder}: out of date — {result.Staleness.Describe()}.");
                    return;

                case GenerationMode.Files:
                    if (result.OutputDirectory == null)
                        return;

                    output.WriteLine(
                        $"{result.AssemblyName} → {GeneratedFileWriter.Relative(result.OutputDirectory, root)}: " +
                        $"{result.Written.Count} written, {result.Deleted.Count} deleted, {result.Unchanged} unchanged.");
                    break;
            }

            if (!verbose)
                return;

            if (result.Timings.Count > 0)
                output.WriteLine("  " + string.Join(", ", result.Timings.Select(t => $"{t.Phase} {t.Milliseconds} ms")));

            foreach (var path in result.Written)
                output.WriteLine($"  + {GeneratedFileWriter.Relative(path, root)}");
            foreach (var path in result.Deleted)
                output.WriteLine($"  - {GeneratedFileWriter.Relative(path, root)}");
        }

        public static bool Failed(AssemblyResult result, bool check) =>
            result.Errors.Count > 0 || (check && result.Staleness != null && !result.Staleness.IsUpToDate);
    }
}
