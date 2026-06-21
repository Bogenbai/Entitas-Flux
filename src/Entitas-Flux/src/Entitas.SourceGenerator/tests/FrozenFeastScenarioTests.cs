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
    /// Reproduces the frozen-feast consumer config (atomic entity API +
    /// IgnoreNamespaces + multi-context [Watched] components) — the combination NOT
    /// covered by the TestFixtures golden baseline — and asserts the generated code
    /// compiles against the real Entitas runtime and exposes the expected API.
    /// </summary>
    public class FrozenFeastScenarioTests
    {
        const string AssemblyAttributes =
            "[assembly: Entitas.CodeGeneration.Attributes.ContextDefinition(\"Game\")]\n" +
            "[assembly: Entitas.CodeGeneration.Attributes.ContextDefinition(\"Inventory\")]\n" +
            "[assembly: Entitas.CodeGeneration.Attributes.EntitasGeneration(" +
            "EntityApi = Entitas.CodeGeneration.Attributes.EntityApiStyle.Atomic, IgnoreNamespaces = true)]\n";

        const string Source = @"
using Entitas;
using Entitas.CodeGeneration.Attributes;

namespace Code.Common {
    // Atomic single-'Value' component, watched, single context.
    [Game, Watched] public class Amount : Entitas.IComponent { public int Value; }

    // Flag component, watched.
    [Game, Watched] public class Processed : Entitas.IComponent { }

    // Watched component spanning two contexts.
    [Game, Inventory, Watched] public class Counter : Entitas.IComponent { public int Value; }
}

namespace My.Deep.Ns {
    // Namespaced component — with IgnoreNamespaces the name must be 'Health', not 'MyDeepNsHealth'.
    [Game] public class Health : Entitas.IComponent { public int Value; }
}
";

        static IEnumerable<MetadataReference> References()
        {
            var tpa = (string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!;
            foreach (var p in tpa.Split(Path.PathSeparator))
                if (!string.IsNullOrEmpty(p) && File.Exists(p))
                    yield return MetadataReference.CreateFromFile(p);
            yield return MetadataReference.CreateFromFile(typeof(Entitas.IComponent).Assembly.Location);
            yield return MetadataReference.CreateFromFile(
                typeof(Entitas.CodeGeneration.Attributes.ContextAttribute).Assembly.Location);
        }

        static (Compilation output, GeneratorDriverRunResult result) Run()
        {
            var trees = new[]
            {
                CSharpSyntaxTree.ParseText(AssemblyAttributes),
                CSharpSyntaxTree.ParseText(Source),
            };
            var compilation = CSharpCompilation.Create("FrozenFeastAsm", trees, References(),
                new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
            var driver = CSharpGeneratorDriver.Create(new EntitasIncrementalGenerator())
                .RunGeneratorsAndUpdateCompilation(compilation, out var output, out _);
            return (output, driver.GetRunResult());
        }

        [Fact]
        public void GeneratedCodeCompiles()
        {
            var (output, _) = Run();
            var errors = output.GetDiagnostics().Where(d => d.Severity == DiagnosticSeverity.Error).ToArray();
            errors.Should().BeEmpty("frozen-feast-style atomic + watched + ignoreNamespaces output must compile");
        }

        [Fact]
        public void EmitsAtomicWatchedAndIgnoreNamespaceApi()
        {
            var (_, result) = Run();
            var all = string.Join("\n", result.GeneratedTrees.Select(t => t.ToString()));

            // Atomic entity API (NOT plain Add-only): direct value property.
            all.Should().Contain("public int Amount { get { return amount.Value; } }");

            // Watched: marker component + HasChanged query + cleanup system.
            all.Should().Contain("class AmountChanged");
            all.Should().Contain("public bool HasChanged(int lookupComponentId)");

            // IgnoreNamespaces: namespaced component collapses to bare name.
            all.Should().Contain("public const int Health =");
            all.Should().NotContain("MyDeepNsHealth");

            // Multi-context watched component lands in both contexts.
            all.Should().Contain("class GameComponentsLookup");
            all.Should().Contain("class InventoryComponentsLookup");
        }
    }
}
