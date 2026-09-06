using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Entitas.SourceGenerator.CodeGeneration;
using FluentAssertions;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Xunit;

namespace Entitas.SourceGenerator.Tests
{
    /// <summary>
    /// A failing generator used to be swallowed by a bare `catch { continue; }`: the API
    /// it owns silently went missing and the user saw only the fallout — hundreds of
    /// CS1061s in their own code. It must report what failed instead.
    /// </summary>
    public class GenerationDiagnosticsTests
    {
        const string Source = @"
[assembly: Entitas.CodeGeneration.Attributes.ContextDefinition(""Game"")]

public sealed class HealthComponent : Entitas.IComponent {
    public int value;
}
";

        sealed class ThrowingGenerator : AbstractGenerator
        {
            public override string Name => "Throwing";

            public override CodeGenFile[] Generate(CodeGeneratorData[] data) =>
                throw new InvalidOperationException("boom");
        }

        [Fact]
        public void Reports_a_failing_generator_instead_of_swallowing_it()
        {
            var input = GenerationInput.From(Compile());

            var result = EntitasGenerators.Generate(input, new AbstractGenerator[]
            {
                new ThrowingGenerator(),
                new ComponentLookupGenerator()
            });

            var diagnostic = result.Diagnostics.Should().ContainSingle().Subject;
            diagnostic.Id.Should().Be(EntitasDiagnostics.GeneratorFailedId);
            diagnostic.Severity.Should().Be(DiagnosticSeverity.Error);
            diagnostic.GetMessage().Should().Contain("ThrowingGenerator").And.Contain("boom");

            // The healthy generator still produced its output.
            result.Sources.Should().NotBeEmpty();
        }

        [Fact]
        public void Reports_a_discovery_failure_instead_of_crashing_the_compiler()
        {
            var input = new GenerationInput(
                false, new[] { "Game" },
                new EntitasGenerationOptions(EntityApiStyle.Plain, false, false, VisualDebuggingStyle.EntityGameObjects, new string[0]),
                new CodeGeneratorData[0],
                "InvalidOperationException: no");

            var result = EntitasGenerators.Generate(input);

            result.Sources.Should().BeEmpty();
            var diagnostic = result.Diagnostics.Should().ContainSingle().Subject;
            diagnostic.Id.Should().Be(EntitasDiagnostics.DiscoveryFailedId);
            diagnostic.GetMessage().Should().Contain("no");
        }

        [Fact]
        public void Says_nothing_when_the_assembly_did_not_opt_in()
        {
            var result = EntitasGenerators.Generate(GenerationInput.Disabled);

            result.Sources.Should().BeEmpty();
            result.Diagnostics.Should().BeEmpty();
        }

        static Compilation Compile() => CSharpCompilation.Create(
            "TestAsm",
            new[] { CSharpSyntaxTree.ParseText(Source, path: "Components.cs") },
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
