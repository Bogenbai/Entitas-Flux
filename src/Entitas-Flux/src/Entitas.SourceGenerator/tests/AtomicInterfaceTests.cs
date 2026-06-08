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
    /// With EntityApi=Atomic the multi-context entity-interface generator must be
    /// dropped: the interface declares the plain lowercase accessor, which the atomic
    /// API hides as a private member under ENTITAS_HIDE_STANDARD_MEMBERS, so the entity
    /// can't implement it (CS0737). Reproduces the frozen-feast failure.
    /// </summary>
    public class AtomicInterfaceTests
    {
        const string Attrs =
            "[assembly: Entitas.CodeGeneration.Attributes.ContextDefinition(\"Game\")]\n" +
            "[assembly: Entitas.CodeGeneration.Attributes.ContextDefinition(\"Quest\")]\n" +
            "[assembly: Entitas.CodeGeneration.Attributes.EntitasGeneration(" +
            "EntityApi = Entitas.CodeGeneration.Attributes.EntityApiStyle.Atomic, IgnoreNamespaces = true)]\n";

        // Multi-context single-'Value' component → would get an I{X}Entity interface.
        const string Source = @"
using Entitas;
using Entitas.CodeGeneration.Attributes;
namespace Proj {
    [Game, Quest] public class CinematicTypeId : IComponent { public int Value; }
}
";

        [Fact]
        public void AtomicMultiContextComponentCompilesWithHiddenStandardMembers()
        {
            var refs = new List<MetadataReference>();
            var tpa = (string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!;
            foreach (var p in tpa.Split(Path.PathSeparator))
                if (!string.IsNullOrEmpty(p) && File.Exists(p))
                    refs.Add(MetadataReference.CreateFromFile(p));
            refs.Add(MetadataReference.CreateFromFile(typeof(Entitas.IComponent).Assembly.Location));
            refs.Add(MetadataReference.CreateFromFile(
                typeof(Entitas.CodeGeneration.Attributes.ContextAttribute).Assembly.Location));

            // ENTITAS_HIDE_STANDARD_MEMBERS makes the atomic lowercase accessor private.
            var parse = new CSharpParseOptions(preprocessorSymbols: new[] { "ENTITAS_HIDE_STANDARD_MEMBERS" });
            var trees = new[]
            {
                CSharpSyntaxTree.ParseText(Attrs, parse),
                CSharpSyntaxTree.ParseText(Source, parse),
            };
            var compilation = CSharpCompilation.Create("AtomicIfaceAsm", trees, refs,
                new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));

            CSharpGeneratorDriver.Create(new EntitasIncrementalGenerator())
                .RunGeneratorsAndUpdateCompilation(compilation, out var output, out _);

            var errors = output.GetDiagnostics()
                .Where(d => d.Severity == DiagnosticSeverity.Error)
                .Select(d => $"{d.Id} {d.GetMessage()}")
                .ToArray();
            errors.Should().BeEmpty("atomic API must not emit an entity interface that hidden members can't satisfy");
        }
    }
}
