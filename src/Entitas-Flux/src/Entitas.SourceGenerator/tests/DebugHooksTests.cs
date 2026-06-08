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
    /// [assembly: EntitasGeneration(DebugHooks = true)] makes the entity-API generator emit
    /// empty partial OnAdd{X}/OnReplace{X} hooks; the consumer implements them in a partial of
    /// the entity to breakpoint a specific mutation. With DebugHooks off (default) nothing is
    /// emitted (covered by GoldenEquivalenceTests staying green).
    /// </summary>
    public class DebugHooksTests
    {
        const string Attrs =
            "[assembly: Entitas.CodeGeneration.Attributes.ContextDefinition(\"Game\")]\n" +
            "[assembly: Entitas.CodeGeneration.Attributes.EntitasGeneration(" +
            "EntityApi = Entitas.CodeGeneration.Attributes.EntityApiStyle.Atomic, IgnoreNamespaces = true, DebugHooks = true)]\n";

        const string Source = @"
using Entitas;
using Entitas.CodeGeneration.Attributes;

namespace Demo { [Game] public class Health : IComponent { public int Value; } }

// The consumer implements the generated hook in their own partial of GameEntity.
public partial class GameEntity
{
    partial void OnReplaceHealth(int newValue)
    {
        if (newValue == 123) { }   // <-- breakpoint here
    }
}
";

        static (Compilation output, GeneratorDriverRunResult result) Run()
        {
            var refs = new List<MetadataReference>();
            var tpa = (string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!;
            foreach (var p in tpa.Split(Path.PathSeparator))
                if (!string.IsNullOrEmpty(p) && File.Exists(p))
                    refs.Add(MetadataReference.CreateFromFile(p));
            refs.Add(MetadataReference.CreateFromFile(typeof(Entitas.IComponent).Assembly.Location));
            refs.Add(MetadataReference.CreateFromFile(
                typeof(Entitas.CodeGeneration.Attributes.ContextAttribute).Assembly.Location));

            var compilation = CSharpCompilation.Create("DebugHooksAsm",
                new[] { CSharpSyntaxTree.ParseText(Attrs), CSharpSyntaxTree.ParseText(Source) }, refs,
                new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
            var driver = CSharpGeneratorDriver.Create(new EntitasIncrementalGenerator())
                .RunGeneratorsAndUpdateCompilation(compilation, out var output, out _);
            return (output, driver.GetRunResult());
        }

        [Fact]
        public void EmitsHookDeclarationsAndCalls()
        {
            var (_, result) = Run();
            var all = string.Join("\n", result.GeneratedTrees.Select(t => t.ToString()));

            all.Should().Contain("partial void OnReplaceHealth(int newValue);");
            all.Should().Contain("partial void OnAddHealth(int newValue);");
            all.Should().Contain("OnReplaceHealth(newValue);");
            all.Should().Contain("OnAddHealth(newValue);");
        }

        [Fact]
        public void GeneratedHooksAndConsumerImplementationCompile()
        {
            var (output, _) = Run();
            var errors = output.GetDiagnostics()
                .Where(d => d.Severity == DiagnosticSeverity.Error)
                .ToArray();
            errors.Should().BeEmpty("the generated partial hook declarations + the consumer's partial implementation must compile together");
        }
    }
}
