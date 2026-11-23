using System;
using System.IO;
using Entitas.Graph.Setup;
using FluentAssertions;
using Xunit;

namespace Entitas.Graph.Tests
{
    /// <summary>
    /// install through the CLI's entry point: what the wrappers rely on (the Unity root from
    /// ENTITAS_GRAPH_ROOT) and the options only the command line has. Console and an
    /// environment variable are the process's own, so these run with the other command tests,
    /// not beside them.
    /// </summary>
    [Collection(nameof(DiffCommandTests))]
    public sealed class InstallCommandTests : IDisposable
    {
        readonly string _temp = Path.Combine(Path.GetTempPath(), "entitas-graph-cli-" + Guid.NewGuid().ToString("N"));

        public void Dispose()
        {
            Environment.SetEnvironmentVariable(Wrappers.RootVariable, null);
            if (Directory.Exists(_temp)) Directory.Delete(_temp, recursive: true);
        }

        string UnityRoot(string name)
        {
            var root = Path.Combine(_temp, name);
            Directory.CreateDirectory(Path.Combine(root, "Assets"));
            return root;
        }

        [Fact]
        public void The_Unity_root_comes_from_the_wrappers_variable_and_an_explicit_r_still_wins()
        {
            var fromVariable = UnityRoot("from-variable");
            var fromOption = UnityRoot("from-option");
            if (AssistantSetup.FindGitRoot(fromVariable) != null) return; // a repository around the temp folder: nothing to test here

            Environment.SetEnvironmentVariable(Wrappers.RootVariable, fromVariable);

            Run("install", "--no-gitignore").Exit.Should().Be(0);
            File.Exists(Path.Combine(fromVariable, "CLAUDE.md")).Should().BeTrue("no -r: the root is what the wrapper set");
            File.Exists(Path.Combine(fromVariable, Wrappers.ShName)).Should().BeTrue();

            Run("install", "--no-gitignore", "-r", fromOption).Exit.Should().Be(0);
            File.Exists(Path.Combine(fromOption, "CLAUDE.md")).Should().BeTrue("-r wins over the variable");
        }

        [Fact]
        public void No_claude_hook_on_the_command_line_leaves_the_settings_alone()
        {
            var root = UnityRoot("game");
            if (AssistantSetup.FindGitRoot(root) != null) return;
            var settings = Path.Combine(root, ".claude", "settings.json");

            Run("install", "--no-gitignore", "--no-claude-hook", "-r", root).Exit.Should().Be(0);
            File.Exists(settings).Should().BeFalse();

            Run("install", "--no-gitignore", "-r", root).Exit.Should().Be(0);
            File.ReadAllText(settings).Should().Contain(ClaudeHook.Flag);
        }

        static (int Exit, string Output, string Error) Run(params string[] args)
        {
            var output = new StringWriter();
            var error = new StringWriter();
            var (stdout, stderr) = (Console.Out, Console.Error);
            Console.SetOut(output);
            Console.SetError(error);
            try
            {
                var exit = (int)typeof(EcsGraph).Assembly.EntryPoint!.Invoke(null, new object[] { args })!;
                return (exit, output.ToString(), error.ToString());
            }
            finally
            {
                Console.SetOut(stdout);
                Console.SetError(stderr);
            }
        }
    }
}
