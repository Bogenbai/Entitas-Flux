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
    /// [assembly: EntitasGeneration(DebugHooks = true)] makes the entity-API generator call the
    /// runtime Entitas.EntitasDebugHooks delegates at the top of each Add/Replace; the consumer
    /// assigns a handler and breakpoints inside it. With DebugHooks off (default) nothing is
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

// The consumer subscribes to the runtime hook and breakpoints inside the handler.
public static class DebugBoot
{
    public static void Install()
    {
        EntitasDebugHooks.OnReplace = (entity, index, value) =>
        {
            if (index == GameComponentsLookup.Health && (int)value == 123) { }   // <-- breakpoint here
        };
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
        public void EmitsRuntimeHookCalls()
        {
            var (_, result) = Run();
            var all = string.Join("\n", result.GeneratedTrees.Select(t => t.ToString()));

            all.Should().Contain("Entitas.EntitasDebugHooks.OnReplace?.Invoke(this, GameComponentsLookup.Health, newValue);");
            all.Should().Contain("Entitas.EntitasDebugHooks.OnAdd?.Invoke(this, GameComponentsLookup.Health, newValue);");
        }

        [Fact]
        public void GeneratedHooksAndConsumerSubscriptionCompile()
        {
            var (output, _) = Run();
            var errors = output.GetDiagnostics()
                .Where(d => d.Severity == DiagnosticSeverity.Error)
                .ToArray();
            errors.Should().BeEmpty("the generated runtime hook calls + the consumer's subscription must compile against the Entitas runtime");
        }
    }
}
