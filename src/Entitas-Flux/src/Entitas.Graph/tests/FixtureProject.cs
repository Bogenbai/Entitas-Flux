using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Entitas.Graph.Analysis;
using Entitas.SourceGenerator;
using FluentAssertions;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace Entitas.Graph.Tests
{
    /// <summary>The Fixture/ project, compiled and turned into a graph once per test run.</summary>
    public static class FixtureProject
    {
        static readonly Lazy<EcsGraph> Cached = new Lazy<EcsGraph>(() => Build(filesMode: false));

        public static EcsGraph Graph => Cached.Value;

        public static string Root
        {
            get
            {
                for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir != null; dir = dir.Parent)
                {
                    var candidate = Path.Combine(dir.FullName, "src", "Entitas.Graph", "tests", "Fixture");
                    if (Directory.Exists(candidate)) return candidate;
                }

                throw new DirectoryNotFoundException("Fixture/ not found from " + AppContext.BaseDirectory);
            }
        }

        public static CSharpCompilation Compile()
        {
            var trees = Directory.EnumerateFiles(Root, "*.cs", SearchOption.AllDirectories)
                .OrderBy(p => p, StringComparer.Ordinal)
                .Select(path => CSharpSyntaxTree.ParseText(File.ReadAllText(path), path: path));
            return CSharpCompilation.Create("Fixture", trees, References(), new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
        }

        /// <summary>
        /// In file mode the generated code is part of the compilation as files (with the
        /// Entitas-Flux marker), exactly as entitas-gen writes it.
        /// </summary>
        public static EcsGraph Build(bool filesMode)
        {
            Compilation compilation = Compile();
            if (filesMode)
            {
                var attributes = CSharpSyntaxTree.ParseText(
                    "[assembly: Entitas.CodeGeneration.Attributes.EntitasGeneration(EntityApi = Entitas.CodeGeneration.Attributes.EntityApiStyle.Atomic, IgnoreNamespaces = true, Output = Entitas.CodeGeneration.Attributes.GenerationOutput.Files)]",
                    path: Path.Combine(Root, "EntitasGeneration.cs"));
                var original = compilation.SyntaxTrees.Single(t => t.FilePath.EndsWith("EntitasGeneration.cs", StringComparison.Ordinal));
                compilation = compilation.ReplaceSyntaxTree(original, CSharpSyntaxTree.ParseText(
                    original.GetText().ToString().Replace(
                        "[assembly: EntitasGeneration(EntityApi = EntityApiStyle.Atomic, IgnoreNamespaces = true)]",
                        "[assembly: EntitasGeneration(EntityApi = EntityApiStyle.Atomic, IgnoreNamespaces = true, Output = GenerationOutput.Files)]"),
                    path: original.FilePath));
                attributes.Should().NotBeNull();

                var sources = EntitasGenerators.Generate(GenerationInput.From(compilation)).Sources;
                compilation = compilation.AddSyntaxTrees(sources.Select(s => CSharpSyntaxTree.ParseText(
                    GeneratedFiles.FileText(s), path: Path.Combine(Root, "Generated", s.FileName))));
            }

            return GraphBuilder.Build(compilation, Root, new GraphOptions());
        }

        public static IEnumerable<MetadataReference> References()
        {
            var tpa = (string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!;
            foreach (var path in tpa.Split(Path.PathSeparator))
                if (!string.IsNullOrEmpty(path) && File.Exists(path))
                    yield return MetadataReference.CreateFromFile(path);

            yield return MetadataReference.CreateFromFile(typeof(Entitas.IComponent).Assembly.Location);
            yield return MetadataReference.CreateFromFile(typeof(Entitas.CodeGeneration.Attributes.ContextAttribute).Assembly.Location);
        }
    }
}
