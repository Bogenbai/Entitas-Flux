using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Entitas.SourceGenerator.CodeGeneration;
using Entitas.SourceGenerator.Discovery;
using FluentAssertions;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Xunit;

namespace Entitas.SourceGenerator.Tests
{
    /// <summary>
    /// Covers the config-driven generator selection (Level 1) and the public,
    /// reusable engine (Level 2). Compilations reference the real Entitas runtime +
    /// code-gen attributes so the config attributes and generated code resolve
    /// against the actual types.
    /// </summary>
    public class ConfigGenerationTests
    {
        const string Usings = "using Entitas.CodeGeneration.Attributes;\n";

        const string Components = @"
public sealed class PositionComponent : Entitas.IComponent {
    public int x;
    public int y;
}

public sealed class CurrentHealthComponent : Entitas.IComponent {
    public int Value;
}

[Event(EventTarget.Self)]
public sealed class ScoreComponent : Entitas.IComponent {
    public int value;
}
";

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

        static Compilation Compile(string assemblyAttributes, string source)
        {
            var tree = CSharpSyntaxTree.ParseText(Usings + assemblyAttributes + source);
            return CSharpCompilation.Create(
                "TestAsm",
                new[] { tree },
                References(),
                new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
        }

        static (Compilation output, string allSource) Run(string assemblyAttributes, string source)
        {
            var compilation = Compile(assemblyAttributes, source);
            var driver = CSharpGeneratorDriver
                .Create(new EntitasIncrementalGenerator())
                .RunGeneratorsAndUpdateCompilation(compilation, out var output, out _);
            var result = driver.GetRunResult();
            var allSource = string.Join("\n", result.GeneratedTrees.Select(t => t.ToString()));
            return (output, allSource);
        }

        static string[] Errors(Compilation output) => output.GetDiagnostics()
            .Where(d => d.Severity == DiagnosticSeverity.Error)
            .Select(d => d.ToString())
            .ToArray();

        // -- Level 1: EntityApi = Atomic swaps the entity API ------------------

        [Fact]
        public void AtomicEntityApiSwapsTheEntityApi()
        {
            const string attrs =
                "[assembly: Entitas.CodeGeneration.Attributes.ContextDefinition(\"Game\")]\n" +
                "[assembly: Entitas.CodeGeneration.Attributes.EntitasGeneration(EntityApi = Entitas.CodeGeneration.Attributes.EntityApiStyle.Atomic)]\n";

            var (output, allSource) = Run(attrs, Components);

            // Atomic accessor for the single-"Value" component.
            allSource.Should().Contain("public int CurrentHealth { get { return currentHealth.Value;");

            // The plain default path emits this exact equality-free Replace overload
            // signature only in STANDARD/atomic-less mode; in atomic mode Replace gains
            // the "if (has...&& ...Value.Equals(newValue))" early-out, so assert the
            // atomic-specific guard is present (proves the atomic template, not plain).
            allSource.Should().Contain("if (hasCurrentHealth && currentHealth.Value.Equals(newValue))");

            Errors(output).Should().BeEmpty("atomic-API generated code must compile against Entitas");
        }

        [Fact]
        public void DefaultEntityApiIsPlainNotAtomic()
        {
            const string attrs =
                "[assembly: Entitas.CodeGeneration.Attributes.ContextDefinition(\"Game\")]\n";

            var (_, allSource) = Run(attrs, Components);

            // Plain API: the atomic uppercase accessor must NOT be emitted.
            allSource.Should().NotContain("public int CurrentHealth { get { return currentHealth.Value;");
            allSource.Should().Contain("AddCurrentHealth(int newValue)");
        }

        // -- Level 1: DisableEntitasGenerator("Event") removes all event output --

        [Fact]
        public void DisableEventGeneratorsRemovesAllEventOutput()
        {
            // The listener component is synthesized at DISCOVERY time from the [Event]
            // attribute (Jenny parity), so disabling only the Event* generators while
            // keeping an [Event] component leaves core generators referencing the
            // un-generated IListener interface. The realistic use of this switch is on
            // an assembly without [Event] components — hence a non-event component set.
            const string nonEventComponents = @"
public sealed class PositionComponent : Entitas.IComponent {
    public int x;
    public int y;
}

public sealed class CurrentHealthComponent : Entitas.IComponent {
    public int Value;
}
";
            const string attrs =
                "[assembly: Entitas.CodeGeneration.Attributes.ContextDefinition(\"Game\")]\n" +
                "[assembly: Entitas.CodeGeneration.Attributes.DisableEntitasGenerator(\"Event\")]\n";

            // Baseline (no disable) over the SAME [Event] component proves the switch
            // is what removes event output, not the input.
            const string eventAttrs =
                "[assembly: Entitas.CodeGeneration.Attributes.ContextDefinition(\"Game\")]\n";
            var (_, withEvents) = Run(eventAttrs, Components);
            withEvents.Should().Contain("EventSystem");
            withEvents.Should().Contain("IScoreListener");

            var (output, allSource) = Run(attrs, nonEventComponents);

            allSource.Should().NotContain("EventSystem");
            allSource.Should().NotContain("IListener");
            allSource.Should().NotContain("EventListener");

            // Non-event output still present and compiling.
            allSource.Should().Contain("class GameComponentsLookup");
            Errors(output).Should().BeEmpty("the remaining (non-event) generated code must still compile");
        }

        // -- Level 1: IgnoreNamespaces changes ComponentName -------------------

        [Fact]
        public void IgnoreNamespacesChangesComponentNameForNamespacedComponent()
        {
            const string namespacedComponent = @"
namespace My.Game {
    public sealed class HealthComponent : Entitas.IComponent {
        public int value;
    }
}
";
            const string baseAttrs =
                "[assembly: Entitas.CodeGeneration.Attributes.ContextDefinition(\"Game\")]\n";
            const string ignoreAttrs =
                "[assembly: Entitas.CodeGeneration.Attributes.ContextDefinition(\"Game\")]\n" +
                "[assembly: Entitas.CodeGeneration.Attributes.EntitasGeneration(IgnoreNamespaces = true)]\n";

            var (_, withNamespaces) = Run(baseAttrs, namespacedComponent);
            var (_, ignored) = Run(ignoreAttrs, namespacedComponent);

            // With namespaces honored the component name is qualified (My.Game.Health).
            withNamespaces.Should().Contain("AddMyGameHealth");
            withNamespaces.Should().NotContain("AddHealth(");

            // With IgnoreNamespaces the short name wins.
            ignored.Should().Contain("AddHealth(");
            ignored.Should().NotContain("AddMyGameHealth");
        }

        // -- Level 2: the engine is publicly reusable end-to-end ---------------

        // A power-user generator defined entirely in the test, depending ONLY on the
        // public engine surface (AbstractGenerator, CodeGenFile, CodeGeneratorData,
        // ComponentData + the data extensions).
        sealed class MyGenerator : AbstractGenerator
        {
            public override string Name => "My Custom Generator";

            public override CodeGenFile[] Generate(CodeGeneratorData[] data) => data
                .OfType<ComponentData>()
                .Where(d => d.ShouldGenerateMethods())
                .Select(d => new CodeGenFile(
                    d.ComponentName() + "Doc.cs",
                    "// Component: " + d.ComponentName() + "\n",
                    GetType().FullName!))
                .ToArray();
        }

        [Fact]
        public void PublicEngineIsReusableWithoutTheFrameworkAnalyzer()
        {
            const string attrs =
                "[assembly: Entitas.CodeGeneration.Attributes.ContextDefinition(\"Game\")]\n";
            var compilation = Compile(attrs, Components);

            // Discover via the public discovery entry point.
            var result = EntitasDiscovery.Discover(compilation);
            var allData = new List<CodeGeneratorData>();
            allData.AddRange(result.Components);

            // Run a user generator over the discovered model.
            var files = new MyGenerator().Generate(allData.ToArray());

            files.Should().NotBeEmpty();
            files.Select(f => f.FileContent).Should()
                .Contain(c => c.Contains("// Component: Position"));
            files.Select(f => f.FileContent).Should()
                .Contain(c => c.Contains("// Component: CurrentHealth"));

            // The public hint-name helper produces unique names (keyed by FileName,
            // as the framework merges fragments sharing a FileName).
            var taken = new HashSet<string>();
            var hintNames = files.Select(f => f.FileName).Distinct()
                .Select(name => EntitasIncrementalGenerator.BuildHintName(name, taken)).ToArray();
            hintNames.Should().OnlyHaveUniqueItems();
        }

        [Fact]
        public void DefaultGeneratorSetReflectsConfig()
        {
            const string atomicAttrs =
                "[assembly: Entitas.CodeGeneration.Attributes.ContextDefinition(\"Game\")]\n" +
                "[assembly: Entitas.CodeGeneration.Attributes.EntitasGeneration(EntityApi = Entitas.CodeGeneration.Attributes.EntityApiStyle.Atomic)]\n" +
                "[assembly: Entitas.CodeGeneration.Attributes.DisableEntitasGenerator(\"Event\")]\n";
            var compilation = Compile(atomicAttrs, Components);

            var defaultSet = EntitasGenerators.Default(compilation);
            var names = defaultSet.Select(g => g.GetType().Name).ToArray();

            // Atomic swapped in, plain swapped out.
            names.Should().Contain(nameof(AtomicComponentEntityApiGenerator));
            names.Should().NotContain(nameof(ComponentEntityApiGenerator));

            // All Event* generators removed by prefix match.
            names.Where(n => n.StartsWith("Event")).Should().BeEmpty();

            // Raw set is unaffected by config.
            var rawNames = EntitasGenerators.All().Select(g => g.GetType().Name).ToArray();
            rawNames.Should().Contain(nameof(ComponentEntityApiGenerator));
            rawNames.Should().NotContain(nameof(AtomicComponentEntityApiGenerator));
            rawNames.Where(n => n.StartsWith("Event")).Should().NotBeEmpty();
        }
    }
}
