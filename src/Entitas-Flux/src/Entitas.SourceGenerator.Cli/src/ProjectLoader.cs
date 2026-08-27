using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Xml.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace Entitas.SourceGenerator.Cli
{
    /// <summary>
    /// Turns a Unity-generated csproj into a Roslyn <see cref="Compilation"/>.
    /// Shared by entitas-gen and entitas-rename (the rename tool compiles this file
    /// via a linked &lt;Compile Include&gt; so both see the exact same file set,
    /// references and defines the analyzer sees during a real Unity build).
    /// </summary>
    public static class ProjectLoader
    {
        /// <summary>
        /// Parses a legacy, non-SDK Unity csproj (MSBuild XML namespace
        /// http://schemas.microsoft.com/developer/msbuild/2003). All element lookups are
        /// namespace-agnostic (compare <see cref="XName.LocalName"/>), and relative paths
        /// are resolved against the csproj's directory with backslashes normalized.
        /// </summary>
        public static ProjectInfo ParseProject(string csprojPath)
        {
            var projectDir = Path.GetDirectoryName(csprojPath)!;
            var doc = XDocument.Load(csprojPath);
            var root = doc.Root ?? throw new InvalidOperationException("csproj has no root element.");

            var sourceFiles = new List<string>();
            var missingSourceFiles = new List<string>();
            var references = new List<string>();
            var defines = new List<string>();

            foreach (var element in root.Descendants())
            {
                switch (element.Name.LocalName)
                {
                    case "Compile":
                    {
                        var include = element.Attribute("Include")?.Value;
                        var resolved = ResolvePath(projectDir, include);
                        if (resolved == null)
                            break;

                        if (File.Exists(resolved))
                            sourceFiles.Add(resolved);
                        else
                            missingSourceFiles.Add(resolved);
                        break;
                    }
                    case "Reference":
                    {
                        var hintPath = element.Elements()
                            .FirstOrDefault(e => e.Name.LocalName == "HintPath")?.Value;
                        var resolved = ResolvePath(projectDir, hintPath);
                        if (resolved != null && File.Exists(resolved))
                            references.Add(resolved);
                        break;
                    }
                    case "DefineConstants":
                    {
                        foreach (var symbol in element.Value.Split(';'))
                        {
                            var trimmed = symbol.Trim();
                            if (trimmed.Length > 0)
                                defines.Add(trimmed);
                        }
                        break;
                    }
                }
            }

            return new ProjectInfo(
                sourceFiles,
                missingSourceFiles,
                references,
                defines.Distinct(StringComparer.Ordinal).ToList());
        }

        static string? ResolvePath(string projectDir, string? raw)
        {
            if (string.IsNullOrWhiteSpace(raw))
                return null;

            var normalized = raw.Replace('\\', Path.DirectorySeparatorChar)
                                .Replace('/', Path.DirectorySeparatorChar)
                                .Trim();

            return Path.IsPathRooted(normalized)
                ? Path.GetFullPath(normalized)
                : Path.GetFullPath(Path.Combine(projectDir, normalized));
        }

        public static CSharpCompilation BuildCompilation(string csprojPath, ProjectInfo project)
        {
            var parseOptions = new CSharpParseOptions(preprocessorSymbols: project.Defines);

            var trees = project.SourceFiles
                .Select(f => CSharpSyntaxTree.ParseText(File.ReadAllText(f), parseOptions, path: f))
                .ToArray();

            var references = project.References
                .Select(r => (MetadataReference)MetadataReference.CreateFromFile(r))
                .ToList();

            // Fall back to the running runtime's BCL only if the csproj resolved no
            // references at all (Unity's csproj references mscorlib/netstandard, so this
            // is just a safety net for malformed inputs).
            if (references.Count == 0)
                references.AddRange(GetRuntimeReferences());

            var assemblyName = Path.GetFileNameWithoutExtension(csprojPath);
            var options = new CSharpCompilationOptions(
                OutputKind.DynamicallyLinkedLibrary,
                allowUnsafe: true);

            return CSharpCompilation.Create(assemblyName, trees, references, options);
        }

        static IEnumerable<MetadataReference> GetRuntimeReferences()
        {
            var tpa = AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES") as string;
            if (string.IsNullOrEmpty(tpa))
                return Array.Empty<MetadataReference>();

            return tpa.Split(Path.PathSeparator)
                .Where(p => p.Length > 0 && File.Exists(p))
                .Select(p => (MetadataReference)MetadataReference.CreateFromFile(p))
                .ToList();
        }

        public sealed class ProjectInfo
        {
            public IReadOnlyList<string> SourceFiles { get; }

            /// <summary>
            /// &lt;Compile&gt; items the csproj lists but that are gone from disk — the
            /// sign of a csproj Unity has not regenerated since files moved.
            /// </summary>
            public IReadOnlyList<string> MissingSourceFiles { get; }

            public IReadOnlyList<string> References { get; }
            public IReadOnlyList<string> Defines { get; }

            public ProjectInfo(
                IReadOnlyList<string> sourceFiles,
                IReadOnlyList<string> missingSourceFiles,
                IReadOnlyList<string> references,
                IReadOnlyList<string> defines)
            {
                SourceFiles = sourceFiles;
                MissingSourceFiles = missingSourceFiles;
                References = references;
                Defines = defines;
            }
        }
    }
}
