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
    /// End-to-end test for the real EntitasIncrementalGenerator: builds a
    /// representative component set, runs the generator, and asserts the resulting
    /// compilation has no errors. This proves (a) partial-class fragment splitting
    /// compiles, (b) cross-references between generated artifacts
    /// (Entity ↔ Lookup ↔ Matcher ↔ Context ↔ Contexts) resolve, and (c) the
    /// un-merged fragments produce no duplicate-definition errors.
    /// </summary>
    public class GeneratorCompileTests
    {
        // A representative, Unity-free component set covering: a plain multi-member
        // component, a single-"Value" component (plain API is the wired default), a
        // flag/tag component, a unique component, a multi-context component, an
        // [Event] component, a [Cleanup] component, a [Watched] component, and an
        // [EntityIndex] member.
        // `using` directives must precede assembly-level attributes, which in turn
        // must precede all type declarations.
        const string Usings = "using Entitas.CodeGeneration.Attributes;\n";

        const string AssemblyAttributes =
            "[assembly: Entitas.CodeGeneration.Attributes.ContextDefinition(\"Game\")]\n" +
            "[assembly: Entitas.CodeGeneration.Attributes.ContextDefinition(\"Input\")]\n";

        const string Source = @"
public sealed class PositionComponent : Entitas.IComponent {
    public int x;
    public int y;
}

public sealed class CurrentHealthComponent : Entitas.IComponent {
    public int Value;
}

public sealed class AnimatingComponent : Entitas.IComponent {
}

[Unique]
public sealed class GameStateComponent : Entitas.IComponent {
    public int value;
}

[Game, Input]
public sealed class SelectedComponent : Entitas.IComponent {
}

[Event(EventTarget.Self)]
public sealed class ScoreComponent : Entitas.IComponent {
    public int value;
}

[Cleanup(CleanupMode.DestroyEntity)]
public sealed class DestroyedComponent : Entitas.IComponent {
}

[Watched]
public sealed class ManaComponent : Entitas.IComponent {
    public int value;
}

public sealed class UserIdComponent : Entitas.IComponent {
    [EntityIndex] public string value;
}
";

        static IEnumerable<MetadataReference> References()
        {
            // BCL reference assemblies that ship with the running runtime.
            var tpa = (string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!;
            foreach (var path in tpa.Split(Path.PathSeparator))
            {
                if (!string.IsNullOrEmpty(path) && File.Exists(path))
                    yield return MetadataReference.CreateFromFile(path);
            }

            // The real Entitas runtime + code-gen attributes, so generated code
            // compiles against the actual types (IComponent, Context<T>, Matcher<T>,
            // ReactiveSystem<T>, the attribute classes, etc.).
            yield return MetadataReference.CreateFromFile(typeof(Entitas.IComponent).Assembly.Location);
            yield return MetadataReference.CreateFromFile(
                typeof(Entitas.CodeGeneration.Attributes.ContextAttribute).Assembly.Location);
        }

        static (Compilation output, GeneratorDriverRunResult result) Run()
        {
            var tree = CSharpSyntaxTree.ParseText(Usings + AssemblyAttributes + Source);

            var compilation = CSharpCompilation.Create(
                "TestAsm",
                new[] { tree },
                References(),
                new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));

            // Sanity: the input itself must compile (modulo the generated members).
            // We don't assert on input diagnostics here because the components are
            // valid on their own; the generator adds the partial halves.

            var driver = CSharpGeneratorDriver
                .Create(new EntitasIncrementalGenerator())
                .RunGeneratorsAndUpdateCompilation(compilation, out var output, out _);

            return (output, driver.GetRunResult());
        }

        [Fact]
        public void GeneratedCodeCompilesWithoutErrors()
        {
            var (output, _) = Run();

            var errors = output.GetDiagnostics()
                .Where(d => d.Severity == DiagnosticSeverity.Error)
                .ToArray();

            errors.Should().BeEmpty(
                "all generated fragments (lookups, entities, contexts, matchers, events, cleanup, watched) " +
                "must compile together against the real Entitas runtime");
        }

        [Fact]
        public void EmitsExpectedCoreTypes()
        {
            var (_, result) = Run();

            var fileNames = result.GeneratedTrees
                .Select(t => Path.GetFileName(t.FilePath))
                .ToArray();
            var allSource = string.Join("\n", result.GeneratedTrees.Select(t => t.ToString()));

            // Core artifacts must be present (hint-name scheme appends .g.cs and a
            // generator discriminator, so match on declared type names in content).
            allSource.Should().Contain("class GameComponentsLookup");
            allSource.Should().Contain("class GameEntity");
            allSource.Should().Contain("class GameMatcher");
            allSource.Should().Contain("class Contexts");

            // Multi-context component produced both context lookups.
            allSource.Should().Contain("class InputComponentsLookup");

            // Every generated tree is uniquely named (no hint-name collisions).
            fileNames.Should().OnlyHaveUniqueItems();
        }

        [Fact]
        public void EmitsFluxSpecificArtifacts()
        {
            var (_, result) = Run();
            var allSource = string.Join("\n", result.GeneratedTrees.Select(t => t.ToString()));

            // Watched component: marker component + HasChanged partial + cleanup system.
            allSource.Should().Contain("class ManaChanged");
            allSource.Should().Contain("public bool HasChanged(int lookupComponentId)");
            allSource.Should().Contain("class RemoveManaChangedGameSystem");

            // Plain entity API is the wired default (NOT atomic): a single-"Value"
            // component still gets the standard component accessor + Add/Replace/Remove
            // (return-type prefix intentionally not asserted — see stale-golden note).
            allSource.Should().Contain("AddCurrentHealth(int newValue)");
            allSource.Should().Contain("CurrentHealthComponent currentHealth");

            // Cleanup (DestroyEntity) system.
            allSource.Should().Contain("class DestroyDestroyedGameSystem");

            // Event listener interface + system. Score is single-context (Game only),
            // so the context prefix is omitted from the listener name.
            allSource.Should().Contain("interface IScoreListener");
            allSource.Should().Contain("class ScoreEventSystem");
        }
    }
}
