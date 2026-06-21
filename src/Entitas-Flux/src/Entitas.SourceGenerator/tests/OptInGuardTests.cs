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
    /// The generator must be a no-op in assemblies that did not opt in — Unity applies
    /// a RoslynAnalyzer to every assembly in the project, so generating Entitas-
    /// referencing output (e.g. Feature.cs) in an unrelated asmdef breaks its build
    /// (CS0246). Opt-in = references Entitas AND has [assembly: ContextDefinition].
    /// </summary>
    public class OptInGuardTests
    {
        static IReadOnlyList<SyntaxTree> RunGetTrees(string source, bool referenceEntitas)
        {
            var refs = new List<MetadataReference>();
            var tpa = (string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!;
            foreach (var p in tpa.Split(Path.PathSeparator))
                if (!string.IsNullOrEmpty(p) && File.Exists(p))
                    refs.Add(MetadataReference.CreateFromFile(p));
            if (referenceEntitas)
            {
                refs.Add(MetadataReference.CreateFromFile(typeof(Entitas.IComponent).Assembly.Location));
                refs.Add(MetadataReference.CreateFromFile(
                    typeof(Entitas.CodeGeneration.Attributes.ContextAttribute).Assembly.Location));
            }

            var compilation = CSharpCompilation.Create("OptInAsm",
                new[] { CSharpSyntaxTree.ParseText(source) }, refs,
                new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));

            return CSharpGeneratorDriver.Create(new EntitasIncrementalGenerator())
                .RunGenerators(compilation).GetRunResult().GeneratedTrees;
        }

        [Fact]
        public void EntitasReferencedButNoContextDefinition_EmitsNothing()
        {
            // References Entitas and even defines a component, but declares no contexts.
            const string src = @"
public sealed class PositionComponent : Entitas.IComponent { public int x; }
";
            RunGetTrees(src, referenceEntitas: true).Should().BeEmpty(
                "an assembly without [assembly: ContextDefinition] has not opted in");
        }

        [Fact]
        public void NonEntitasAssembly_EmitsNothing()
        {
            // A plain assembly that does not reference Entitas at all (e.g. an unrelated
            // asmdef) must not get Feature.cs or any other Entitas-referencing output.
            const string src = @"namespace Other { public class Whatever { } }";
            RunGetTrees(src, referenceEntitas: false).Should().BeEmpty(
                "an assembly that does not reference Entitas must produce no generated code");
        }
    }
}
