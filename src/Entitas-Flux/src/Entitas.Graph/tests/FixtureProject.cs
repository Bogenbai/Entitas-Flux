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
        public static EcsGraph Graph => FixtureFolder.Sample.Graph;

        public static string Root => FixtureFolder.Sample.Root;

        public static CSharpCompilation Compile() => FixtureFolder.Sample.Compile();

        /// <summary>
        /// In file mode the generated code is part of the compilation as files (with the
        /// Entitas-Flux marker), exactly as entitas-gen writes it.
        /// </summary>
        public static EcsGraph Build(bool filesMode) => FixtureFolder.Sample.Build(filesMode);

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

    /// <summary>
    /// A small Unity-like project in a folder of tests/, compiled with the real generator. Fixture/
    /// mirrors the naming of the project the tool was first tried on; FixtureNeutral/ is a space
    /// port with names of its own, to show nothing depends on them.
    /// </summary>
    public sealed class FixtureFolder
    {
        public static readonly FixtureFolder Sample = new FixtureFolder("Fixture", "Fixture", "EntitasGeneration.cs",
            "[assembly: EntitasGeneration(EntityApi = EntityApiStyle.Atomic, IgnoreNamespaces = true)]");

        public static readonly FixtureFolder Neutral = new FixtureFolder("FixtureNeutral", "Harbor", "PortGeneration.cs",
            "[assembly: EntitasGeneration(IgnoreNamespaces = true)]");

        readonly string _folder;
        readonly string _assembly;
        readonly string _generationFile;
        readonly string _generationAttribute;
        readonly Lazy<EcsGraph> _graph;

        FixtureFolder(string folder, string assembly, string generationFile, string generationAttribute)
        {
            _folder = folder;
            _assembly = assembly;
            _generationFile = generationFile;
            _generationAttribute = generationAttribute;
            _graph = new Lazy<EcsGraph>(() => Build(filesMode: false));
        }

        public EcsGraph Graph => _graph.Value;

        public string Root
        {
            get
            {
                for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir != null; dir = dir.Parent)
                {
                    var candidate = Path.Combine(dir.FullName, "src", "Entitas.Graph", "tests", _folder);
                    if (Directory.Exists(candidate)) return candidate;
                }

                throw new DirectoryNotFoundException(_folder + "/ not found from " + AppContext.BaseDirectory);
            }
        }

        public CSharpCompilation Compile()
        {
            var trees = Directory.EnumerateFiles(Root, "*.cs", SearchOption.AllDirectories)
                .OrderBy(p => p, StringComparer.Ordinal)
                .Select(path => CSharpSyntaxTree.ParseText(File.ReadAllText(path), path: path));
            return CSharpCompilation.Create(_assembly, trees, FixtureProject.References(), new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
        }

        /// <summary>
        /// In file mode the generated code is part of the compilation as files (with the
        /// Entitas-Flux marker), exactly as entitas-gen writes it.
        /// </summary>
        public EcsGraph Build(bool filesMode = false, GraphOptions? options = null)
        {
            Compilation compilation = Compile();
            if (filesMode)
            {
                var original = compilation.SyntaxTrees.Single(t => t.FilePath.EndsWith(_generationFile, StringComparison.Ordinal));
                var text = original.GetText().ToString();
                text.Should().Contain(_generationAttribute);
                compilation = compilation.ReplaceSyntaxTree(original, CSharpSyntaxTree.ParseText(
                    text.Replace(_generationAttribute, _generationAttribute.Replace(")]", ", Output = GenerationOutput.Files)]")),
                    path: original.FilePath));

                var sources = EntitasGenerators.Generate(GenerationInput.From(compilation)).Sources;
                compilation = compilation.AddSyntaxTrees(sources.Select(s => CSharpSyntaxTree.ParseText(
                    GeneratedFiles.FileText(s), path: Path.Combine(Root, "Generated", s.FileName))));
            }

            return GraphBuilder.Build(compilation, Root, options ?? new GraphOptions());
        }
    }
}
