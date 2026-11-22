using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;
using FluentAssertions;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace Entitas.SourceGenerator.Tests
{
    /// <summary>The two ways generated code reaches the compiler.</summary>
    public enum Output
    {
        /// <summary>The source generator emits it during compilation (the default).</summary>
        SourceGenerator,

        /// <summary>It is on disk as files — Output = GenerationOutput.Files — and the source generator stands down.</summary>
        Files
    }

    /// <summary>
    /// Compiles test sources the way a real project would see them in either mode, so the
    /// same test proves both. In <see cref="Output.Files"/> the generated sources are added
    /// as files exactly as the writer renders them (marker line included) and the source
    /// generator still runs on top — it must add nothing and report nothing, or the
    /// assembly would hold every type twice.
    /// </summary>
    public static class OutputModes
    {
        public static IEnumerable<object[]> Both => new[]
        {
            new object[] { Output.SourceGenerator },
            new object[] { Output.Files }
        };

        public static IEnumerable<MetadataReference> References()
        {
            var tpa = (string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!;
            foreach (var path in tpa.Split(Path.PathSeparator))
            {
                if (!string.IsNullOrEmpty(path) && File.Exists(path))
                    yield return MetadataReference.CreateFromFile(path);
            }

            yield return MetadataReference.CreateFromFile(typeof(Entitas.IComponent).Assembly.Location);
            yield return MetadataReference.CreateFromFile(
                typeof(Entitas.CodeGeneration.Attributes.ContextAttribute).Assembly.Location);
        }

        /// <summary>Adds <c>Output = GenerationOutput.Files</c> to the source's assembly config.</summary>
        public static string WithFilesOutput(string source)
        {
            const string files = "Output = Entitas.CodeGeneration.Attributes.GenerationOutput.Files";

            var existing = new Regex(@"\[assembly:\s*(Entitas\.CodeGeneration\.Attributes\.)?EntitasGeneration\(");
            var match = existing.Match(source);
            if (match.Success)
            {
                var insertAt = match.Index + match.Length;
                var separator = source[insertAt] == ')' ? string.Empty : ", ";
                return source.Insert(insertAt, files + separator);
            }

            // Next to the other assembly attributes: after any using directives, before types.
            var line = "[assembly: Entitas.CodeGeneration.Attributes.EntitasGeneration(" + files + ")]\n";
            var firstAssemblyAttribute = source.IndexOf("[assembly:", StringComparison.Ordinal);
            return firstAssemblyAttribute < 0 ? line + source : source.Insert(firstAssemblyAttribute, line);
        }

        public static CSharpCompilation Compile(string assemblyName, params (string Path, string Source)[] files) =>
            CSharpCompilation.Create(
                assemblyName,
                files.Select(f => CSharpSyntaxTree.ParseText(f.Source, path: f.Path)),
                References(),
                new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));

        /// <summary>
        /// The generated files for a compilation, as trees placed where the writer would put
        /// them (relative to <paramref name="outputDirectory"/>).
        /// </summary>
        public static SyntaxTree[] GeneratedFileTrees(Compilation compilation, string outputDirectory)
        {
            var result = EntitasGenerators.Generate(GenerationInput.From(compilation));
            result.Diagnostics.Should().BeEmpty();

            var parseOptions = compilation.SyntaxTrees.First().Options;
            return result.Sources
                .Select(source => CSharpSyntaxTree.ParseText(
                    GeneratedFiles.FileText(source),
                    (CSharpParseOptions)parseOptions,
                    path: outputDirectory + "/" + source.FileName))
                .ToArray();
        }

        /// <summary>
        /// Generates and compiles <paramref name="source"/> (a single file named Components.cs)
        /// in the given mode and returns the finished compilation, asserting it is error-free.
        /// </summary>
        public static Compilation Build(string assemblyName, string source, Output output)
        {
            var name = assemblyName + "_" + output;

            if (output == Output.SourceGenerator)
            {
                var compilation = Compile(name, ("Components.cs", source));
                CSharpGeneratorDriver
                    .Create(new EntitasIncrementalGenerator())
                    .RunGeneratorsAndUpdateCompilation(compilation, out var generated, out _);

                return AssertCompiles(generated);
            }

            var withFiles = Compile(name, ("Components.cs", WithFilesOutput(source)));

            // No folder in the declaring file's path: the default output folder is "Generated".
            withFiles = withFiles.AddSyntaxTrees(GeneratedFileTrees(withFiles, GeneratedFiles.DefaultFolderName));

            CSharpGeneratorDriver
                .Create(new EntitasIncrementalGenerator())
                .RunGeneratorsAndUpdateCompilation(withFiles, out var final, out var diagnostics);

            final.SyntaxTrees.Count().Should().Be(withFiles.SyntaxTrees.Count(),
                "in file mode the source generator must not emit the code again");
            diagnostics.Should().BeEmpty("the files are exactly what generation produces, so nothing is stale");

            return AssertCompiles(final);
        }

        public static Assembly Load(Compilation compilation)
        {
            using var stream = new MemoryStream();
            var result = compilation.Emit(stream);
            result.Success.Should().BeTrue(string.Join("\n", result.Diagnostics
                .Where(d => d.Severity == DiagnosticSeverity.Error)
                .Select(d => d.ToString())));

            return Assembly.Load(stream.ToArray());
        }

        static Compilation AssertCompiles(Compilation compilation)
        {
            var errors = compilation.GetDiagnostics()
                .Where(d => d.Severity == DiagnosticSeverity.Error)
                .Select(d => d.ToString())
                .ToArray();

            errors.Should().BeEmpty();
            return compilation;
        }
    }
}
