using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using FluentAssertions;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Xunit;

namespace Entitas.SourceGenerator.Tests
{
    /// <summary>
    /// Approval test over the whole generated output for the TestFixtures component set.
    ///
    /// This replaces the Jenny equivalence test. That test existed to gate ONE event —
    /// the migration from the Jenny CLI to the source generator — and it did its job:
    /// output was proven byte-identical to Jenny's, and that proof is preserved in git
    /// history (see tests/JennyBaseline before this commit). Keeping it as the permanent
    /// reference would have frozen Jenny's quirks along with its behaviour, including the
    /// ones we now want to fix.
    ///
    /// From here the reference is our own committed output: any change to generation
    /// shows up as a reviewable diff in this folder instead of silently passing or
    /// failing against a foreign baseline.
    ///
    /// To accept an intended change: run the tests with ENTITAS_UPDATE_SNAPSHOT=1 and
    /// review the diff.
    /// </summary>
    public class GeneratedOutputSnapshotTests
    {
        const string AssemblyAttributes =
            "[assembly: Entitas.CodeGeneration.Attributes.ContextDefinition(\"Game\")]\n" +
            "[assembly: Entitas.CodeGeneration.Attributes.ContextDefinition(\"Test\")]\n" +
            "[assembly: Entitas.CodeGeneration.Attributes.ContextDefinition(\"Test2\")]\n";

        static string SnapshotDir => Path.Combine(
            RepoRoot(), "src", "Entitas.SourceGenerator", "tests", "Snapshot");

        [Fact]
        public void GeneratedOutputMatchesTheCommittedSnapshot()
        {
            var generated = RunGenerator();

            if (Environment.GetEnvironmentVariable("ENTITAS_UPDATE_SNAPSHOT") == "1")
            {
                WriteSnapshot(generated);
                return;
            }

            Directory.Exists(SnapshotDir).Should().BeTrue(
                "the snapshot is committed; regenerate it with ENTITAS_UPDATE_SNAPSHOT=1");

            var expected = Directory.EnumerateFiles(SnapshotDir, "*.cs", SearchOption.TopDirectoryOnly)
                .ToDictionary(path => Path.GetFileName(path)!, File.ReadAllText, StringComparer.Ordinal);

            generated.Keys.Should().BeEquivalentTo(expected.Keys,
                "the set of generated files must match the snapshot");

            foreach (var file in generated.OrderBy(f => f.Key, StringComparer.Ordinal))
            {
                Normalize(file.Value).Should().Be(Normalize(expected[file.Key]),
                    $"generated {file.Key} must match the snapshot");
            }
        }

        /// <summary>
        /// The same snapshot, through the file mode: what the writer puts on disk must be the
        /// source generator's output byte for byte, plus the marker line — and once those
        /// files are in the assembly, generating again must produce them unchanged (discovery
        /// must not read its own output back) while the source generator adds nothing.
        /// </summary>
        [Fact]
        public void FileModeOutputMatchesTheCommittedSnapshot()
        {
            var compilation = FixturesCompilation(AssemblyAttributes + FilesOutput);
            var sources = EntitasGenerators.Generate(GenerationInput.From(compilation)).Sources;

            var expected = Directory.EnumerateFiles(SnapshotDir, "*.cs", SearchOption.TopDirectoryOnly)
                .ToDictionary(path => Path.GetFileName(path)!, File.ReadAllText, StringComparer.Ordinal);

            var hintNames = new HashSet<string>();
            var written = sources.ToDictionary(
                source => EntitasIncrementalGenerator.BuildHintName(source.FileName, hintNames),
                GeneratedFiles.FileText,
                StringComparer.Ordinal);

            written.Keys.Should().BeEquivalentTo(expected.Keys, "file mode writes one file per source the generator adds");
            foreach (var file in written.OrderBy(f => f.Key, StringComparer.Ordinal))
            {
                GeneratedFiles.IsGenerated(file.Value).Should().BeTrue();
                Normalize(GeneratedFiles.WithoutMarker(file.Value)).Should().Be(Normalize(expected[file.Key]),
                    $"{file.Key} on disk must be exactly what the source generator emits");
            }

            // Fixpoint: with the files in the assembly, generation reproduces them exactly.
            var withFiles = compilation.AddSyntaxTrees(sources.Select(source =>
                CSharpSyntaxTree.ParseText(GeneratedFiles.FileText(source), path: "Generated/" + source.FileName)));

            EntitasGenerators.Generate(GenerationInput.From(withFiles)).Sources
                .Select(source => (source.FileName, source.Content))
                .Should().Equal(sources.Select(source => (source.FileName, source.Content)));

            var run = CSharpGeneratorDriver.Create(new EntitasIncrementalGenerator()).RunGenerators(withFiles).GetRunResult();
            run.GeneratedTrees.Should().BeEmpty("the source generator stands down in file mode");
            run.Diagnostics.Should().BeEmpty("the files are current");
        }

        const string FilesOutput =
            "[assembly: Entitas.CodeGeneration.Attributes.EntitasGeneration(Output = Entitas.CodeGeneration.Attributes.GenerationOutput.Files)]\n";

        static void WriteSnapshot(IReadOnlyDictionary<string, string> generated)
        {
            if (Directory.Exists(SnapshotDir))
                Directory.Delete(SnapshotDir, recursive: true);

            Directory.CreateDirectory(SnapshotDir);
            foreach (var file in generated)
                File.WriteAllText(Path.Combine(SnapshotDir, file.Key), file.Value);
        }

        static CSharpCompilation FixturesCompilation(string assemblyAttributes) =>
            CSharpCompilation.Create(
                "TestFixturesAsm",
                InputTrees(assemblyAttributes),
                References(),
                new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));

        static Dictionary<string, string> RunGenerator()
        {
            var compilation = FixturesCompilation(AssemblyAttributes);

            var result = CSharpGeneratorDriver
                .Create(new EntitasIncrementalGenerator())
                .RunGenerators(compilation)
                .GetRunResult();

            return result.GeneratedTrees.ToDictionary(
                tree => Path.GetFileName(tree.FilePath),
                tree => tree.ToString(),
                StringComparer.Ordinal);
        }

        /// <summary>Line endings only — the content itself is compared as written.</summary>
        static string Normalize(string source) => source.Replace("\r\n", "\n").TrimEnd() + "\n";

        static IReadOnlyList<SyntaxTree> InputTrees(string assemblyAttributes)
        {
            var fixturesDir = Path.Combine(RepoRoot(), "Tests", "TestFixtures", "Fixtures");
            var trees = Directory.EnumerateFiles(fixturesDir, "*.cs", SearchOption.AllDirectories)
                .OrderBy(p => p, StringComparer.Ordinal)
                .Select(file => CSharpSyntaxTree.ParseText(File.ReadAllText(file), path: file))
                .ToList();

            // A path, so the default output folder ("Generated" beside it) can be worked out.
            trees.Add(CSharpSyntaxTree.ParseText(assemblyAttributes, path: "EntitasGeneration.cs"));
            return trees;
        }

        static IEnumerable<MetadataReference> References()
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

        static string RepoRoot()
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir != null)
            {
                if (Directory.Exists(Path.Combine(dir.FullName, "Tests", "TestFixtures", "Fixtures")))
                    return dir.FullName;
                dir = dir.Parent;
            }

            throw new DirectoryNotFoundException(
                "Could not locate TestFixtures/Fixtures from " + AppContext.BaseDirectory);
        }
    }
}
