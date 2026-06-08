using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using FluentAssertions;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Xunit;
using Xunit.Abstractions;

namespace Entitas.SourceGenerator.Tests
{
    /// <summary>
    /// Reproduces the frozen-feast duplicate-definition failures: same SHORT component
    /// name reused across different contexts (with IgnoreNamespaces=true), where the
    /// components live in namespaces NAMED AFTER the contexts (e.g. a `Proj.Inventory`
    /// namespace). During generation the context attribute is unresolved and binds to
    /// that namespace; resolving it by ToString() ("Proj.Inventory") instead of the
    /// simple name ("Inventory") used to drop the context and pile every such component
    /// into the default context, producing duplicate component/system definitions.
    /// </summary>
    public class MultiContextDuplicateReproTests
    {
        readonly ITestOutputHelper _out;
        public MultiContextDuplicateReproTests(ITestOutputHelper o) => _out = o;

        const string Attrs =
            "[assembly: Entitas.CodeGeneration.Attributes.ContextDefinition(\"Game\")]\n" +
            "[assembly: Entitas.CodeGeneration.Attributes.ContextDefinition(\"Quest\")]\n" +
            "[assembly: Entitas.CodeGeneration.Attributes.ContextDefinition(\"Inventory\")]\n" +
            "[assembly: Entitas.CodeGeneration.Attributes.EntitasGeneration(" +
            "EntityApi = Entitas.CodeGeneration.Attributes.EntityApiStyle.Atomic, IgnoreNamespaces = true)]\n";

        // Namespaces are deliberately named after the contexts (Proj.Quest, Proj.Inventory)
        // to reproduce the unresolved-attribute-binds-to-namespace case from frozen-feast.
        const string Source = @"
using Entitas;
using Entitas.CodeGeneration.Attributes;

namespace Proj.Common {
    [Game, Watched] public class Completed : IComponent { }
    [Game, Watched] public class Amount : IComponent { public int Value; }
    [Game] public class OwnerId : IComponent { [EntityIndex] public int Value; }
    [Game] public class LinkedQuestGuid : IComponent { [EntityIndex] public string Value; }
    [Game, Watched] public class Id : IComponent { [PrimaryEntityIndex] public int Value; }
}
namespace Proj.Quest {
    [Quest, Watched] public class Completed : IComponent { }
    [Quest] public class Id : IComponent { [PrimaryEntityIndex] public int Value; }
    [Quest] public class LinkedQuestGuid : IComponent { [EntityIndex] public string Value; }
}
namespace Proj.Inventory {
    [Inventory, Watched] public class Amount : IComponent { public int Value; }
    [Inventory] public class Id : IComponent { [PrimaryEntityIndex] public int Value; }
    [Inventory] public class OwnerId : IComponent { [EntityIndex] public int Value; }
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

            var compilation = CSharpCompilation.Create("ReproAsm",
                new[] { CSharpSyntaxTree.ParseText(Attrs), CSharpSyntaxTree.ParseText(Source) }, refs,
                new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
            var driver = CSharpGeneratorDriver.Create(new EntitasIncrementalGenerator())
                .RunGeneratorsAndUpdateCompilation(compilation, out var output, out _);
            return (output, driver.GetRunResult());
        }

        [Fact]
        public void NoDuplicateDefinitions()
        {
            var (output, _) = Run();
            var errors = output.GetDiagnostics()
                .Where(d => d.Severity == DiagnosticSeverity.Error)
                .Select(d => $"{d.Id} {d.GetMessage()}")
                .Distinct()
                .ToArray();
            if (errors.Length > 0)
                _out.WriteLine(string.Join("\n", errors));
            errors.Should().BeEmpty();
        }
    }
}
