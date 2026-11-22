using System;
using System.Collections.Generic;

namespace Entitas.SourceGenerator.Cli
{
    // The JSON the Unity editor and entitas-gen exchange (--request / --response). The
    // editor side is Entitas.Unity.Editor's EntitasCodeGeneration, which serializes the
    // same shape with UnityEngine.JsonUtility — hence public fields, camelCase names and
    // no nulls (JsonUtility writes "" for a missing string).

    /// <summary>What the editor asks for: the project's assemblies as Unity compiles them.</summary>
    public sealed class GenerationRequest
    {
        /// <summary>Absolute path of the Unity project (the folder holding Assets/).</summary>
        public string projectRoot = string.Empty;

        /// <summary>Only report whether the files are current; write nothing.</summary>
        public bool check;

        public List<RequestedAssembly> assemblies = new List<RequestedAssembly>();
    }

    /// <summary>
    /// One assembly, straight from UnityEditor.Compilation.CompilationPipeline — the source
    /// of truth for what Unity compiles, unlike a csproj an IDE package may not have
    /// regenerated since the last file was added.
    /// </summary>
    public sealed class RequestedAssembly
    {
        public string name = string.Empty;

        /// <summary>Project-relative (Assets/…) or absolute.</summary>
        public List<string> sourceFiles = new List<string>();

        /// <summary>Absolute paths of every referenced assembly.</summary>
        public List<string> references = new List<string>();

        public List<string> defines = new List<string>();

        /// <summary>e.g. "9.0"; empty for the compiler's latest.</summary>
        public string languageVersion = string.Empty;
    }

    public sealed class GenerationResponse
    {
        /// <summary>Set when the request itself could not be handled.</summary>
        public string error = string.Empty;

        public long milliseconds;

        public List<AssemblyReport> assemblies = new List<AssemblyReport>();
    }

    /// <summary>Paths are project-relative with forward slashes, the form AssetDatabase uses.</summary>
    public sealed class AssemblyReport
    {
        public string name = string.Empty;

        /// <summary>"Files", "SourceGenerator" or "NotConfigured".</summary>
        public string mode = string.Empty;

        public string outputDirectory = string.Empty;
        public List<string> written = new List<string>();
        public List<string> deleted = new List<string>();
        public int unchanged;
        public List<string> errors = new List<string>();
        public List<string> warnings = new List<string>();

        /// <summary>Files whose edits can change the generated code — the editor's trigger list.</summary>
        public List<string> componentFiles = new List<string>();

        /// <summary>Check mode: what is out of date, empty when current.</summary>
        public string stale = string.Empty;

        public static AssemblyReport From(AssemblyResult result, string projectRoot)
        {
            var report = new AssemblyReport
            {
                name = result.AssemblyName,
                mode = result.Mode.ToString(),
                outputDirectory = result.OutputDirectory == null
                    ? string.Empty
                    : GeneratedFileWriter.Relative(result.OutputDirectory, projectRoot),
                unchanged = result.Unchanged,
                stale = result.Staleness == null || result.Staleness.IsUpToDate
                    ? string.Empty
                    : result.Staleness.Describe()
            };

            foreach (var path in result.Written)
                report.written.Add(GeneratedFileWriter.Relative(path, projectRoot));
            foreach (var path in result.Deleted)
                report.deleted.Add(GeneratedFileWriter.Relative(path, projectRoot));

            report.errors.AddRange(result.Errors);
            report.warnings.AddRange(result.Warnings);
            report.componentFiles.AddRange(result.ComponentFiles);
            return report;
        }
    }
}
