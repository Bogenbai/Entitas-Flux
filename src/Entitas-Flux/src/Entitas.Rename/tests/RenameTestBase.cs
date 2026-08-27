using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Entitas.SourceGenerator;
using Entitas.SourceGenerator.Rename;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace Entitas.Rename.Tests
{
    /// <summary>
    /// Builds in-memory compilations that reference the real Entitas runtime and
    /// code-gen attributes, so the plan is computed against genuinely generated code.
    /// </summary>
    public abstract class RenameTestBase
    {
        protected const string Usings = "using Entitas.CodeGeneration.Attributes;\n";
        protected const string ContextDefinition = "[assembly: ContextDefinition(\"Game\")]\n";

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

        protected static Compilation Compile(params (string Path, string Source)[] files) =>
            Compile("TestAsm", files);

        protected static Compilation Compile(string assemblyName, params (string Path, string Source)[] files)
        {
            var trees = files
                .Select(f => CSharpSyntaxTree.ParseText(f.Source, path: f.Path))
                .ToArray();

            return CSharpCompilation.Create(
                assemblyName,
                trees,
                References(),
                new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
        }

        /// <summary>Applies a plan to the in-memory sources and returns the new file set.</summary>
        protected static Dictionary<string, string> ApplyPlan(
            Compilation compilation,
            RenamePlan plan)
        {
            var result = compilation.SyntaxTrees
                .ToDictionary(t => t.FilePath, t => t.GetText().ToString(), StringComparer.Ordinal);

            foreach (var file in plan.Files)
                result[file.Path] = RenameEngine.Apply(result[file.Path], file);

            return result;
        }

        /// <summary>Compiler errors after regenerating the renamed sources.</summary>
        protected static string[] ErrorsAfterRename(Compilation compilation, RenamePlan plan)
        {
            var renamed = ApplyPlan(compilation, plan);
            var updated = Compile(renamed.Select(kvp => (kvp.Key, kvp.Value)).ToArray());

            CSharpGeneratorDriver
                .Create(new EntitasIncrementalGenerator())
                .RunGeneratorsAndUpdateCompilation(updated, out var output, out _);

            return output.GetDiagnostics()
                .Where(d => d.Severity == DiagnosticSeverity.Error)
                .Select(d => d.ToString())
                .ToArray();
        }
    }
}
