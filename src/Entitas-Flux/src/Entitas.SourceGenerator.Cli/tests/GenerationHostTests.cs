using System.IO;
using System.Linq;
using FluentAssertions;
using Xunit;

namespace Entitas.SourceGenerator.Cli.Tests
{
    /// <summary>How entitas-gen finds and compiles what to generate: Unity's request, and csproj files.</summary>
    public class GenerationHostTests
    {
        const string Config = @"using Entitas.CodeGeneration.Attributes;
[assembly: ContextDefinition(""Game"")]
[assembly: EntitasGeneration(Output = GenerationOutput.Files)]
";

        const string Components = "public sealed class HealthComponent : Entitas.IComponent { public int value; }\n";

        [Fact]
        public void Handles_the_request_the_unity_editor_sends()
        {
            using var project = new UnityProjectFixture();
            project.Write("Assets/Code/EntitasGeneration.cs", Config);
            project.Write("Assets/Code/Components.cs", Components);
            project.Write("Assets/Editor/Tool.cs", "public class Tool { }");

            var request = new GenerationRequest { projectRoot = project.Root };
            request.assemblies.Add(new RequestedAssembly
            {
                name = "Assembly-CSharp",
                sourceFiles = { "Assets/Code/EntitasGeneration.cs", "Assets/Code/Components.cs" },
                references = UnityProjectFixture.References().ToList()
            });
            // An assembly that does not declare contexts is skipped without being compiled.
            request.assemblies.Add(new RequestedAssembly
            {
                name = "Assembly-CSharp-Editor",
                sourceFiles = { "Assets/Editor/Tool.cs" },
                references = UnityProjectFixture.References().ToList()
            });

            var response = GenerationHost.RunRequest(request);

            response.error.Should().BeEmpty();
            var report = response.assemblies.Should().ContainSingle().Subject;
            report.name.Should().Be("Assembly-CSharp");
            report.mode.Should().Be("Files");
            report.outputDirectory.Should().Be("Assets/Code/Generated");
            report.written.Should().Contain("Assets/Code/Generated/Game/GameEntity.cs");
            report.componentFiles.Should().BeEquivalentTo("Assets/Code/Components.cs", "Assets/Code/EntitasGeneration.cs");
            project.Exists("Assets/Code/Generated/Game/GameEntity.cs").Should().BeTrue();
        }

        [Fact]
        public void Finds_the_csproj_that_declares_contexts_and_skips_the_player_twin()
        {
            using var project = new UnityProjectFixture();
            project.Write("Assets/Code/EntitasGeneration.cs", Config);
            project.Write("Assets/Code/Components.cs", Components);
            project.Write("Assets/Other/Other.cs", "public class Other { }");

            var entitas = typeof(Entitas.IComponent).Assembly.Location;
            project.Write("Assembly-CSharp.csproj", Csproj(entitas, "Assets/Code/EntitasGeneration.cs", "Assets/Code/Components.cs"));
            project.Write("Assembly-CSharp.Player.csproj", Csproj(entitas, "Assets/Code/EntitasGeneration.cs", "Assets/Code/Components.cs"));
            project.Write("Other.csproj", Csproj(entitas, "Assets/Other/Other.cs"));

            GenerationHost.FindGeneratingProjects(project.Root).Select(Path.GetFileName)
                .Should().Equal("Assembly-CSharp.csproj");
        }

        [Fact]
        public void Resolves_asmdef_project_references_to_what_unity_compiled()
        {
            using var project = new UnityProjectFixture();
            project.Write("Library/ScriptAssemblies/Game.Core.dll", "not really a dll");
            project.Write("Assembly-CSharp.csproj", @"<Project ToolsVersion=""4.0"" xmlns=""http://schemas.microsoft.com/developer/msbuild/2003"">
  <ItemGroup><ProjectReference Include=""Game.Core.csproj""><Name>Game.Core</Name></ProjectReference></ItemGroup>
  <PropertyGroup><LangVersion>9.0</LangVersion></PropertyGroup>
</Project>");

            var info = ProjectLoader.ParseProject(project.PathOf("Assembly-CSharp.csproj"));

            info.References.Should().Contain(project.PathOf("Library/ScriptAssemblies/Game.Core.dll"));
            info.LanguageVersion.Should().Be("9.0");
        }

        static string Csproj(string entitasDll, params string[] sources) =>
            $@"<Project ToolsVersion=""4.0"" xmlns=""http://schemas.microsoft.com/developer/msbuild/2003"">
  <ItemGroup>
{string.Join("\n", sources.Select(s => $@"    <Compile Include=""{s}"" />"))}
  </ItemGroup>
  <ItemGroup><Reference Include=""Entitas""><HintPath>{entitasDll}</HintPath></Reference></ItemGroup>
</Project>";
    }
}
