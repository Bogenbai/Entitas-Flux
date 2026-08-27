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
                .ToDictionary(Path.GetFileName, File.ReadAllText, StringComparer.Ordinal);

            generated.Keys.Should().BeEquivalentTo(expected.Keys,
                "the set of generated files must match the snapshot");

            foreach (var file in generated.OrderBy(f => f.Key, StringComparer.Ordinal))
            {
                Normalize(file.Value).Should().Be(Normalize(expected[file.Key]),
                    $"generated {file.Key} must match the snapshot");
            }
        }

        static void WriteSnapshot(IReadOnlyDictionary<string, string> generated)
        {
            if (Directory.Exists(SnapshotDir))
                Directory.Delete(SnapshotDir, recursive: true);

            Directory.CreateDirectory(SnapshotDir);
            foreach (var file in generated)
                File.WriteAllText(Path.Combine(SnapshotDir, file.Key), file.Value);
        }

        static Dictionary<string, string> RunGenerator()
        {
            var compilation = CSharpCompilation.Create(
                "TestFixturesAsm",
                InputTrees(),
                References(),
                new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));

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

        static IReadOnlyList<SyntaxTree> InputTrees()
        {
            var fixturesDir = Path.Combine(RepoRoot(), "Tests", "TestFixtures", "Fixtures");
            var trees = Directory.EnumerateFiles(fixturesDir, "*.cs", SearchOption.AllDirectories)
                .OrderBy(p => p, StringComparer.Ordinal)
                .Select(file => CSharpSyntaxTree.ParseText(File.ReadAllText(file), path: file))
                .ToList();

            trees.Add(CSharpSyntaxTree.ParseText(AssemblyAttributes));
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
