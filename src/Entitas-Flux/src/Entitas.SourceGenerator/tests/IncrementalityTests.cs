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
    /// The generator hangs off <see cref="GenerationInput"/>, a value compared by
    /// content, so Roslyn can skip generation entirely when an edit does not affect it.
    /// These tests pin that behaviour down: edits inside method bodies must be cached,
    /// edits to a component must not.
    /// </summary>
    public class IncrementalityTests
    {
        const string Components = @"
[assembly: Entitas.CodeGeneration.Attributes.ContextDefinition(""Game"")]

public sealed class HealthComponent : Entitas.IComponent {
    public int value;
}
";

        const string SystemBefore = @"
public sealed class DamageSystem {
    public void Execute(GameEntity entity) {
        entity.ReplaceHealth(1);
    }
}
";

        // Same component surface; only a method body changes.
        const string SystemAfter = @"
public sealed class DamageSystem {
    public void Execute(GameEntity entity) {
        var damage = 2;
        entity.ReplaceHealth(damage);
    }
}
";

        const string ComponentsWithExtraField = @"
[assembly: Entitas.CodeGeneration.Attributes.ContextDefinition(""Game"")]

public sealed class HealthComponent : Entitas.IComponent {
    public int value;
    public int max;
}
";

        [Fact]
        public void Skips_generation_when_an_edit_does_not_touch_components()
        {
            var reasons = RunTwice(Components, SystemBefore, Components, SystemAfter);

            reasons.Should().NotBeEmpty();
            reasons.Should().AllSatisfy(reason => reason.Should().BeOneOf(
                IncrementalStepRunReason.Cached, IncrementalStepRunReason.Unchanged));
        }

        [Fact]
        public void Regenerates_when_a_component_changes()
        {
            var reasons = RunTwice(Components, SystemBefore, ComponentsWithExtraField, SystemBefore);

            reasons.Should().Contain(reason =>
                reason == IncrementalStepRunReason.Modified || reason == IncrementalStepRunReason.New);
        }

        /// <summary>Runs the generator twice on the same driver and returns the second run's output-step reasons.</summary>
        static IReadOnlyList<IncrementalStepRunReason> RunTwice(
            string componentsBefore, string systemBefore, string componentsAfter, string systemAfter)
        {
            GeneratorDriver driver = CSharpGeneratorDriver.Create(
                new[] { new EntitasIncrementalGenerator().AsSourceGenerator() },
                driverOptions: new GeneratorDriverOptions(
                    IncrementalGeneratorOutputKind.None, trackIncrementalGeneratorSteps: true));

            driver = driver.RunGenerators(Compile(componentsBefore, systemBefore));
            driver = driver.RunGenerators(Compile(componentsAfter, systemAfter));

            return driver.GetRunResult().Results.Single()
                .TrackedOutputSteps
                .SelectMany(step => step.Value)
                .SelectMany(step => step.Outputs)
                .Select(output => output.Reason)
                .ToArray();
        }

        static Compilation Compile(string components, string system) => CSharpCompilation.Create(
            "TestAsm",
            new[]
            {
                CSharpSyntaxTree.ParseText(components, path: "Components.cs"),
                CSharpSyntaxTree.ParseText(system, path: "DamageSystem.cs")
            },
            References(),
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));

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
    }
}
