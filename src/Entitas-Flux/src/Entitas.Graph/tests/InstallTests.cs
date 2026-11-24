using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Entitas.Graph.Setup;
using FluentAssertions;
using Xunit;

namespace Entitas.Graph.Tests
{
    /// <summary>
    /// entitas-graph install / uninstall on throwaway folders laid out like a real project: a
    /// repository root with the Unity root in a subfolder, the tool inside the Unity project.
    /// </summary>
    public sealed class InstallTests : IDisposable
    {
        static readonly string[] None = Array.Empty<string>();

        readonly string _repo;
        readonly string _unity;
        readonly string _tool;

        public InstallTests()
        {
            _repo = Path.Combine(Path.GetTempPath(), "entitas-graph-install-" + Guid.NewGuid().ToString("N"));
            _unity = Path.Combine(_repo, "src", "game");
            Directory.CreateDirectory(Path.Combine(_repo, ".git"));
            Directory.CreateDirectory(Path.Combine(_unity, "Assets"));
            _tool = Path.Combine(_unity, "Assets", "Entitas", "Entitas", "Tools~", "entitas-graph.dll");
        }

        public void Dispose() => Directory.Delete(_repo, recursive: true);

        string RepoFile(string name) => Path.Combine(_repo, name);

        SetupResult Install(params string[] targets) => AssistantSetup.Install(_unity, targets, gitignore: true, _tool);

        [Fact]
        public void Writes_the_block_into_CLAUDE_md_in_the_git_root_with_paths_relative_to_it()
        {
            var result = Install();

            result.Files.Should().Contain(f => f.Path == RepoFile("CLAUDE.md") && f.Outcome == FileOutcome.Created);
            var text = File.ReadAllText(RepoFile("CLAUDE.md"));
            text.Should().StartWith(InstructionBlock.Begin + "\n").And.EndWith(InstructionBlock.End + "\n");
            text.Should().Contain("    ./entitas-graph who-reads <Component>        (Windows cmd or PowerShell: .\\entitas-graph.cmd; --help lists every command and option)\n");
            text.Should().NotContain("dotnet ").And.NotContain(" -r ", "the wrappers find the tool and the Unity root");
            text.Should().Contain("`src/game/entitas-graph-out/GRAPH_REPORT.md`");
            File.Exists(Path.Combine(_unity, "CLAUDE.md")).Should().BeFalse();
            result.Files.Should().Contain(f => f.Path == RepoFile("entitas-graph") && f.Outcome == FileOutcome.Created);
            result.Files.Should().Contain(f => f.Path == RepoFile("entitas-graph.cmd") && f.Outcome == FileOutcome.Created);
            File.ReadAllText(RepoFile("entitas-graph")).Should().Contain("root=\"$here/src/game\"\n");
            File.ReadAllText(RepoFile("entitas-graph.cmd")).Should().Contain("set \"root=%~dp0src\\game\"\r\n");
            result.Notes.Should().ContainSingle().Which.Should().Contain("approve the project's MCP server", "the only note: what the new server needs from the user");
        }

        [Fact]
        public void The_block_is_short_and_names_every_command_and_the_cap()
        {
            var block = InstructionBlock.Build(_repo, _unity, _tool, _repo);
            var lines = block.Split('\n');

            lines.Length.Should().BeLessOrEqualTo(15);
            foreach (var command in new[] { "who-creates", "who-reads", "lifecycle", "systems-of", "path", "explain", "query \"<words>\"", "diff --commit", "diff --ref", "report" })
                block.Should().Contain(command);
            block.Should().Contain("~2000 tokens").And.Contain("Never read graph.json");
            block.Should().Contain("--help lists every command and option", "options such as --budget and --depth are not listed in the block");
            block.Should().Contain("Before changing code, open the files an answer names", "the graph is where to look, not a reason to skip the code");
            block.Should().Contain("mcp__entitas-graph__*", "a subagent without Bash cannot run the command, but can list the server's tools");
        }

        [Fact]
        public void A_block_from_another_version_is_reported_so_build_can_say_run_install()
        {
            AssistantSetup.OutdatedBlocks(_unity, _tool).Should().BeEmpty("there is no block yet, and install is opt-in");

            Install();
            AssistantSetup.OutdatedBlocks(_unity, _tool).Should().BeEmpty("the block is what install writes now");

            // An older package listed fewer commands.
            var claude = RepoFile("CLAUDE.md");
            File.WriteAllText(claude, File.ReadAllText(claude).Replace("diff --commit <sha> (what one commit changed); ", string.Empty));
            AssistantSetup.OutdatedBlocks(_unity, _tool).Should().Equal(claude);

            var elsewhere = Path.Combine(Path.GetTempPath(), "entitas-flux-build", "entitas-graph.dll");
            AssistantSetup.OutdatedBlocks(_unity, elsewhere).Should().BeEmpty("a tool outside the repository would write an absolute path");

            Install();
            AssistantSetup.OutdatedBlocks(_unity, _tool).Should().BeEmpty("install brought it up to date");
        }

        [Fact]
        public void A_block_beside_the_Unity_root_names_the_wrappers_in_the_repository_root()
        {
            var block = InstructionBlock.Build(_unity, _unity, _tool, _repo);

            block.Should().Contain("    ../../entitas-graph who-reads <Component>        (Windows cmd or PowerShell: ..\\..\\entitas-graph.cmd; --help lists every command and option)\n");
            block.Should().Contain("`entitas-graph-out/GRAPH_REPORT.md`");
        }

        [Fact]
        public void A_package_cache_install_is_found_by_the_wrapper_whatever_hash_its_folder_has()
        {
            var tool = Path.Combine(_unity, "Library", "PackageCache", "com.bogenbai.entitas-flux@1a2b3c4d5e", "Tools~", "entitas-graph.dll");

            AssistantSetup.Install(_unity, None, gitignore: false, tool);

            File.ReadAllText(RepoFile("CLAUDE.md")).Should().Contain("    ./entitas-graph who-reads <Component>").And.NotContain("PackageCache");
            var sh = File.ReadAllText(RepoFile("entitas-graph"));
            sh.Should().Contain("\"$root\"/Library/PackageCache/*/Tools~/entitas-graph.dll").And.NotContain("1a2b3c4d5e", "the hash changes with every update");
            File.ReadAllText(RepoFile("entitas-graph.cmd")).Should().Contain("for /d %%p in (\"%root%\\Library\\PackageCache\\*\")").And.NotContain("1a2b3c4d5e");
        }

        [Fact]
        public void Paths_in_an_explicit_target_are_relative_to_that_file()
        {
            var target = Path.Combine(_repo, "docs", "AI.md");

            Install(target);

            File.ReadAllText(target).Should().Contain("    ../entitas-graph who-reads <Component>        (Windows cmd or PowerShell: ..\\entitas-graph.cmd; --help lists every command and option)\n");
            File.Exists(RepoFile("CLAUDE.md")).Should().BeFalse();
            File.Exists(RepoFile("entitas-graph")).Should().BeTrue("the wrappers stay in the repository root");
        }

        [Fact]
        public void A_Unity_root_with_a_space_is_quoted_in_both_wrappers()
        {
            var unity = Path.Combine(_repo, "My Game");
            Directory.CreateDirectory(Path.Combine(unity, "Assets"));
            var tool = Path.Combine(unity, "Library", "PackageCache", "com.bogenbai.entitas-flux@abc", "Tools~", "entitas-graph.dll");

            Wrappers.Sh(_repo, unity, tool, _repo).Should().Contain("root=\"$here/My Game\"\n");
            Wrappers.Cmd(_repo, unity, tool, _repo).Should().Contain("set \"root=%~dp0My Game\"\r\n");
            InstructionBlock.Build(_repo, unity, tool, _repo).Should().Contain("`My Game/entitas-graph-out/GRAPH_REPORT.md`");
        }

        [Fact]
        public void A_tool_outside_the_repository_is_named_by_absolute_path_with_a_note()
        {
            var tool = Path.Combine(Path.GetTempPath(), "elsewhere", "entitas-graph.dll");

            var result = AssistantSetup.Install(_unity, None, gitignore: false, tool);

            File.ReadAllText(RepoFile("entitas-graph")).Should().Contain($"for tool in \"{Path.GetFullPath(tool).Replace('\\', '/')}\" ");
            result.Notes.Should().ContainSingle(n => n.Contains("absolute path"));
        }

        [Fact]
        public void The_sh_wrapper_runs_the_tool_it_finds_with_the_Unity_root_whatever_layout_the_project_has()
        {
            if (OperatingSystem.IsWindows()) return; // the sh wrapper is for macOS, Linux and Git Bash

            // A stand-in for dotnet that prints what it was given.
            var bin = Path.Combine(_repo, "bin");
            Directory.CreateDirectory(bin);
            File.WriteAllText(Path.Combine(bin, "dotnet"), "#!/bin/sh\necho \"tool=$1 args=$2 $3 root=$ENTITAS_GRAPH_ROOT\"\n");
            Run("chmod", "+x", Path.Combine(bin, "dotnet"));

            string Graph(string args)
            {
                var start = new System.Diagnostics.ProcessStartInfo(RepoFile("entitas-graph")) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
                foreach (var arg in args.Split(' ')) start.ArgumentList.Add(arg);
                start.Environment["PATH"] = bin + Path.PathSeparator + Environment.GetEnvironmentVariable("PATH");
                using var process = System.Diagnostics.Process.Start(start)!;
                var output = process.StandardOutput.ReadToEnd() + process.StandardError.ReadToEnd();
                process.WaitForExit();
                return output.Trim();
            }

            // Nothing installed yet: the wrapper says where it looked.
            Install();
            Run("sh", "-n", RepoFile("entitas-graph"));
            Run("test", "-x", RepoFile("entitas-graph"));
            Graph("who-reads Health").Should().StartWith("entitas-graph: entitas-graph.dll not found in " + _repo + "/src/game");

            // Installed by hand: the tool sits in Assets/Entitas/Entitas/Tools~.
            Directory.CreateDirectory(Path.GetDirectoryName(_tool)!);
            File.WriteAllText(_tool, string.Empty);
            Graph("who-reads Health").Should().Be($"tool={_tool} args=who-reads Health root={_repo}/src/game");

            // Updated through the Package Manager: a package folder with a hash, found without a new install.
            var package = Path.Combine(_unity, "Library", "PackageCache", "com.bogenbai.entitas-flux@9f8e7d", "Tools~");
            Directory.CreateDirectory(package);
            File.WriteAllText(Path.Combine(package, "entitas-graph.dll"), string.Empty);
            Graph("flow HireRequest").Should().Be($"tool={package}/entitas-graph.dll args=flow HireRequest root={_repo}/src/game");
        }

        static void Run(string file, params string[] args)
        {
            var start = new System.Diagnostics.ProcessStartInfo(file) { UseShellExecute = false };
            foreach (var arg in args) start.ArgumentList.Add(arg);
            using var process = System.Diagnostics.Process.Start(start)!;
            process.WaitForExit();
            process.ExitCode.Should().Be(0, $"{file} {string.Join(" ", args)} succeeds");
        }

        const string ProjectSettings = "{\n  \"hooks\": {\n    \"SessionStart\": [\n      {\n        \"hooks\": [\n          {\n            \"type\": \"command\",\n            \"command\": \"\\\"$CLAUDE_PROJECT_DIR\\\"/.claude/scripts/session.sh\"\n          }\n        ]\n      }\n    ],\n    \"PreToolUse\": [\n      {\n        \"matcher\": \"Edit|Write\",\n        \"hooks\": [\n          {\n            \"type\": \"command\",\n            \"command\": \"echo 'BLOCKED: no edits in Assets/Plugins/.' >&2; exit 2\"\n          }\n        ]\n      }\n    ]\n  }\n}\n";

        string Settings => Path.Combine(_repo, ".claude", "settings.json");

        string Servers => Path.Combine(_repo, ".mcp.json");

        const string ProjectServers = "{\n  \"mcpServers\": {\n    \"UnityMCP\": {\n      \"command\": \"uvx\",\n      \"args\": [\"mcp-for-unity\"]\n    }\n  }\n}\n";

        [Fact]
        public void Install_registers_the_MCP_server_next_to_the_projects_own_and_uninstall_takes_only_it_out()
        {
            File.WriteAllText(Servers, ProjectServers);

            var result = Install();

            var text = File.ReadAllText(Servers);
            text.Should().Contain("\"UnityMCP\"").And.Contain("mcp-for-unity", "the project's own servers stay");
            var server = System.Text.Json.JsonDocument.Parse(text).RootElement.GetProperty("mcpServers").GetProperty("entitas-graph");
            server.GetProperty("command").GetString().Should().Be("git", "git is the one command a Claude Code machine has on every OS");
            server.GetProperty("args").EnumerateArray().Select(a => a.GetString()).Should().Equal("-c", "alias.entitas-graph=!sh ./entitas-graph mcp", "entitas-graph");
            result.Notes.Should().Contain(n => n.Contains("mcp__entitas-graph__*"), "the agents that need it have to list it");

            Install().Files.Should().Contain(f => f.Path == Servers && f.Outcome == FileOutcome.Unchanged);

            AssistantSetup.Uninstall(_unity, None);
            System.Text.Json.JsonDocument.Parse(File.ReadAllText(Servers)).RootElement.GetProperty("mcpServers")
                .EnumerateObject().Select(p => p.Name).Should().Equal("UnityMCP");

            File.Delete(Servers);
            Install();
            AssistantSetup.Uninstall(_unity, None).Files.Should().Contain(f => f.Path == Servers && f.Outcome == FileOutcome.Deleted, "nothing else was in it");
            File.Exists(Servers).Should().BeFalse();
        }

        [Fact]
        public void An_entitas_graph_server_install_did_not_write_stops_it_before_writing_anything()
        {
            var theirs = "{ \"mcpServers\": { \"entitas-graph\": { \"command\": \"node\", \"args\": [\"their-graph.js\"] } } }\n";
            File.WriteAllText(Servers, theirs);

            var install = () => Install();

            install.Should().Throw<InvalidOperationException>().WithMessage("*.mcp.json*not written by entitas-graph install*");
            File.ReadAllText(Servers).Should().Be(theirs);
            File.Exists(RepoFile("CLAUDE.md")).Should().BeFalse();
        }

        [Fact]
        public void Without_mcp_install_leaves_the_servers_file_alone_and_an_older_server_of_ours_is_reported()
        {
            AssistantSetup.Install(_unity, None, gitignore: false, _tool, mcp: false);
            File.Exists(Servers).Should().BeFalse();

            Install();
            AssistantSetup.OutdatedBlocks(_unity, _tool).Should().BeEmpty();
            File.WriteAllText(Servers, File.ReadAllText(Servers).Replace("\"command\": \"git\"", "\"command\": \"/usr/bin/git\""));
            AssistantSetup.OutdatedBlocks(_unity, _tool).Should().Contain(Servers, "build tells the user to run install again");
        }

        [Fact]
        public void The_sh_entry_0_6_2_wrote_is_recognised_and_replaced()
        {
            File.WriteAllText(Servers, "{\n  \"mcpServers\": {\n    \"entitas-graph\": {\n      \"type\": \"stdio\",\n      \"command\": \"sh\",\n      \"args\": [\n        \"-c\",\n" +
                                       "        \"root=$(git rev-parse --show-toplevel 2>/dev/null) || root=.; exec \\\"$root/entitas-graph\\\" mcp\"\n      ]\n    }\n  }\n}\n");
            AssistantSetup.OutdatedBlocks(_unity, _tool).Should().Contain(Servers, "it does not start on Windows");

            Install().Files.Should().Contain(f => f.Path == Servers && f.Outcome == FileOutcome.Updated);
            System.Text.Json.JsonDocument.Parse(File.ReadAllText(Servers)).RootElement.GetProperty("mcpServers").GetProperty("entitas-graph")
                .GetProperty("command").GetString().Should().Be("git");
        }

        [Fact]
        public void Install_pins_the_wrappers_line_endings_and_keeps_what_gitattributes_had()
        {
            var attributes = RepoFile(".gitattributes");
            File.WriteAllText(attributes, "*.png binary\r\n/entitas-graph.cmd   text  eol=crlf\r\n");

            var change = Install().Files.Single(f => f.Path == attributes);

            change.Outcome.Should().Be(FileOutcome.Updated);
            File.ReadAllText(attributes).Should().Be("*.png binary\r\n/entitas-graph.cmd   text  eol=crlf\r\n/entitas-graph text eol=lf\r\n",
                "sh stops at a carriage return, which Git for Windows checks out by default; the cmd line was there already");
            Install().Files.Single(f => f.Path == attributes).Outcome.Should().Be(FileOutcome.Unchanged);

            File.Delete(attributes);
            AssistantSetup.Install(_unity, None, gitignore: false, _tool);
            File.Exists(attributes).Should().BeFalse("--no-gitignore leaves both git files alone");
        }

        [Fact]
        public void The_registered_command_reaches_the_server_from_any_folder_of_the_repository()
        {
            if (OperatingSystem.IsWindows()) return; // runs the sh wrapper; the same git alias is what Windows gets

            // Claude Code finds .mcp.json from any folder below it but starts the server where it
            // was started itself: a real repository, and a start two folders down.
            var repo = Path.Combine(_repo, "real");
            var unity = Path.Combine(repo, "src", "game");
            Directory.CreateDirectory(Path.Combine(unity, "Assets", "Code"));
            Run("git", "init", "-q", repo);
            AssistantSetup.Install(unity, None, gitignore: false, Path.Combine(AppContext.BaseDirectory, "entitas-graph.dll"));

            // Exactly what the file says to run, as Claude Code runs it: no shell around it.
            var server = System.Text.Json.JsonDocument.Parse(File.ReadAllText(Path.Combine(repo, ".mcp.json"))).RootElement.GetProperty("mcpServers").GetProperty("entitas-graph");
            var start = new System.Diagnostics.ProcessStartInfo(server.GetProperty("command").GetString()!) { RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, WorkingDirectory = Path.Combine(unity, "Assets", "Code") };
            foreach (var argument in server.GetProperty("args").EnumerateArray()) start.ArgumentList.Add(argument.GetString()!);
            using var process = System.Diagnostics.Process.Start(start)!;
            process.StandardInput.Write("{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"initialize\",\"params\":{\"protocolVersion\":\"2025-06-18\"}}\n{\"jsonrpc\":\"2.0\",\"id\":2,\"method\":\"tools/list\"}\n");
            process.StandardInput.Close();
            var output = process.StandardOutput.ReadToEnd();
            process.WaitForExit();

            var lines = output.Split('\n', StringSplitOptions.RemoveEmptyEntries);
            lines.Should().HaveCount(2, process.StandardError.ReadToEnd());
            lines[0].Should().Contain("\"serverInfo\":{\"name\":\"entitas-graph\"");
            lines[1].Should().Contain("\"name\":\"who_reads\"");
            output.Should().Match(o => o.All(c => c < 128), "every reply is ASCII, whatever code page sits between the tool and the client");
        }

        [Fact]
        public void Install_adds_the_Claude_hook_next_to_the_projects_own_and_uninstall_takes_only_it_out()
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Settings)!);
            File.WriteAllText(Settings, ProjectSettings);

            Install().Files.Should().Contain(f => f.Path == Settings && f.Outcome == FileOutcome.Updated);
            var text = File.ReadAllText(Settings);
            text.Should().Contain("\"matcher\": \"Edit|Write\"").And.Contain("session.sh", "the project's own hooks stay");
            text.Should().Contain("\"matcher\": \"Read|Grep|Glob|Bash\"").And.Contain("\"command\": \"\\\"$CLAUDE_PROJECT_DIR\\\"/entitas-graph --claude-hook\"");
            text.Should().EndWith("}\n");

            Install().Files.Should().Contain(f => f.Path == Settings && f.Outcome == FileOutcome.Unchanged);
            AssistantSetup.OutdatedBlocks(_unity, _tool).Should().BeEmpty();

            AssistantSetup.Uninstall(_unity, None).Files.Should().Contain(f => f.Path == Settings && f.Outcome == FileOutcome.Updated);
            File.ReadAllText(Settings).Should().Be(ProjectSettings, "the file is back as the project wrote it");
        }

        [Fact]
        public void An_older_hook_of_ours_is_reported_and_replaced_not_added_twice()
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Settings)!);
            var older = ProjectSettings.Replace("\n    ]\n  }\n}\n",
                ",\n      {\n        \"matcher\": \"Read|Bash\",\n        \"hooks\": [\n          {\n            \"type\": \"command\",\n            \"command\": \"\\\"$CLAUDE_PROJECT_DIR\\\"/entitas-graph --claude-hook\"\n          }\n        ]\n      }\n    ]\n  }\n}\n");
            older.Should().Contain("\"Read|Bash\"", "the test starts from an older entry");
            File.WriteAllText(Settings, older);

            AssistantSetup.OutdatedBlocks(_unity, _tool).Should().Contain(Settings, "build says to run install again");

            Install();
            var text = File.ReadAllText(Settings);
            Count(text, "--claude-hook").Should().Be(1, "the older entry is replaced");
            text.Should().Contain("\"Read|Grep|Glob|Bash\"").And.NotContain("\"Read|Bash\"").And.Contain("\"Edit|Write\"");
            AssistantSetup.OutdatedBlocks(_unity, _tool).Should().NotContain(Settings);
        }

        [Fact]
        public void Settings_with_CRLF_line_breaks_keep_them()
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Settings)!);
            var crlf = ProjectSettings.Replace("\n", "\r\n");
            File.WriteAllText(Settings, crlf);

            Install();
            var text = File.ReadAllText(Settings);
            text.Should().Contain("--claude-hook").And.EndWith("}\r\n");
            text.Replace("\r\n", string.Empty).Should().NotContain("\n", "every line break stays CRLF");

            AssistantSetup.Uninstall(_unity, None);
            File.ReadAllText(Settings).Should().Be(crlf);
        }

        [Fact]
        public void Without_settings_install_creates_them_uninstall_deletes_them_and_an_opt_out_leaves_them_alone()
        {
            AssistantSetup.Install(_unity, None, gitignore: false, _tool, claudeHook: false);
            File.Exists(Settings).Should().BeFalse();

            Install();
            File.ReadAllText(Settings).Should().Contain("--claude-hook");

            AssistantSetup.Uninstall(_unity, None).Files.Should().Contain(f => f.Path == Settings && f.Outcome == FileOutcome.Deleted);
            File.Exists(Settings).Should().BeFalse();
        }

        [Fact]
        public void Settings_that_are_not_JSON_stop_install_before_it_writes_anything()
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Settings)!);
            File.WriteAllText(Settings, "{ \"hooks\": ");

            var install = () => Install();

            install.Should().Throw<InvalidOperationException>().WithMessage("*settings.json*");
            File.Exists(RepoFile("CLAUDE.md")).Should().BeFalse();
            File.Exists(RepoFile("entitas-graph")).Should().BeFalse();
        }

        [Fact]
        public void The_hook_stops_reads_of_graph_json_and_lets_everything_else_through()
        {
            if (OperatingSystem.IsWindows()) return; // Claude Code runs hooks through bash

            // The real tool, this build's: the wrapper hands it every call that mentions the folder.
            AssistantSetup.Install(_unity, None, gitignore: false, Path.Combine(AppContext.BaseDirectory, "entitas-graph.dll"));
            string Call(string tool, string input) => $"{{\"session_id\":\"s\",\"cwd\":\"{_repo}\",\"hook_event_name\":\"PreToolUse\",\"tool_name\":\"{tool}\",\"tool_input\":{input}}}";
            var output = $"{_repo}/src/game/entitas-graph-out";

            var read = Hook(Call("Read", $"{{\"file_path\":\"{output}/graph.json\"}}"));
            read.Code.Should().Be(2, "exit 2 blocks the call and hands stderr to Claude");
            read.Error.Should().Contain("./entitas-graph who-reads <Component>");
            Hook(Call("Bash", "{\"command\":\"cat src/game/entitas-graph-out/graph.json | head\"}")).Code.Should().Be(2);
            Hook(Call("Bash", "{\"command\":\"head -c 4000 src/game/entitas-graph-out/graph.html\"}")).Code.Should().Be(2);
            Hook(Call("Grep", $"{{\"pattern\":\"Health\",\"path\":\"{output}\"}}")).Code.Should().Be(2, "a search there goes through graph.json");

            Hook(Call("Read", $"{{\"file_path\":\"{output}/GRAPH_REPORT.md\"}}")).Code.Should().Be(0, "the report is meant to be read");
            Hook(Call("Bash", "{\"command\":\"./entitas-graph diff src/game/entitas-graph-out/graph.json old.json\"}")).Code.Should().Be(0, "the tool reads it itself");
            Hook(Call("Bash", "{\"command\":\"git commit -m \\\"Ignore entitas-graph-out/: graph.json is 1.4 M tokens\\\"\"}")).Code.Should().Be(0, "a message only names them");
            Hook(Call("Bash", "{\"command\":\"gh pr create --body \\\"$(cat <<'EOF'\\nKeeps (entitas-graph-out/graph.json) out\\nEOF\\n)\\\"\"}")).Code.Should().Be(0);
            Hook(Call("Bash", "{\"command\":\"git status\"}")).Code.Should().Be(0);
            Hook(Call("Read", $"{{\"file_path\":\"{_repo}/src/game/Assets/Code/Health.cs\"}}")).Code.Should().Be(0);
        }

        [Fact]
        public void The_hook_starts_dotnet_only_for_a_call_that_mentions_the_output_folder()
        {
            if (OperatingSystem.IsWindows()) return;

            // A stand-in for dotnet that leaves a mark and stops everything.
            var bin = Path.Combine(_repo, "bin");
            var mark = Path.Combine(_repo, "dotnet-ran");
            Directory.CreateDirectory(bin);
            File.WriteAllText(Path.Combine(bin, "dotnet"), $"#!/bin/sh\ntouch \"{mark}\"\nexit 2\n");
            Run("chmod", "+x", Path.Combine(bin, "dotnet"));
            Install();
            var git = "{\"tool_name\":\"Bash\",\"tool_input\":{\"command\":\"git status\"}}";
            var graph = "{\"tool_name\":\"Bash\",\"tool_input\":{\"command\":\"cat entitas-graph-out/graph.json\"}}";

            Hook(graph, bin).Code.Should().Be(0, "no tool installed yet: nothing decides, so nothing is stopped");

            Directory.CreateDirectory(Path.GetDirectoryName(_tool)!);
            File.WriteAllText(_tool, string.Empty);
            Hook(git, bin).Code.Should().Be(0);
            File.Exists(mark).Should().BeFalse("a call that never mentions entitas-graph-out is settled in sh");
            Hook(graph, bin).Code.Should().Be(2, "the tool's answer is the hook's");
            File.Exists(mark).Should().BeTrue();
        }

        (int Code, string Error) Hook(string call, string? path = null)
        {
            var start = new System.Diagnostics.ProcessStartInfo(RepoFile("entitas-graph")) { RedirectStandardInput = true, RedirectStandardError = true, RedirectStandardOutput = true, UseShellExecute = false };
            start.ArgumentList.Add("--claude-hook");
            if (path != null) start.Environment["PATH"] = path + Path.PathSeparator + Environment.GetEnvironmentVariable("PATH");
            using var process = System.Diagnostics.Process.Start(start)!;
            process.StandardInput.Write(call);
            process.StandardInput.Close();
            var error = process.StandardError.ReadToEnd();
            process.WaitForExit();
            return (process.ExitCode, error);
        }

        [Fact]
        public void A_file_named_like_a_wrapper_that_install_did_not_write_stops_it_before_writing_anything()
        {
            File.WriteAllText(RepoFile("entitas-graph"), "#!/bin/sh\necho mine\n");

            var install = () => Install();

            install.Should().Throw<SetupException>().WithMessage("*was not written by entitas-graph install*");
            File.Exists(RepoFile("CLAUDE.md")).Should().BeFalse();
            File.ReadAllText(RepoFile("entitas-graph")).Should().Be("#!/bin/sh\necho mine\n");

            // Uninstall leaves it alone too.
            AssistantSetup.Uninstall(_unity, None).Files.Should().Contain(f => f.Path == RepoFile("entitas-graph") && f.Outcome == FileOutcome.Unchanged);
            File.Exists(RepoFile("entitas-graph")).Should().BeTrue();
        }

        [Fact]
        public void Uninstall_removes_the_wrappers_and_an_outdated_one_is_reported()
        {
            Install();
            AssistantSetup.OutdatedBlocks(_unity, _tool).Should().BeEmpty();

            File.AppendAllText(RepoFile("entitas-graph.cmd"), "rem an older version\r\n");
            AssistantSetup.OutdatedBlocks(_unity, _tool).Should().Equal(RepoFile("entitas-graph.cmd"));

            var result = AssistantSetup.Uninstall(_unity, None);

            result.Files.Should().Contain(f => f.Path == RepoFile("entitas-graph") && f.Outcome == FileOutcome.Deleted);
            result.Files.Should().Contain(f => f.Path == RepoFile("entitas-graph.cmd") && f.Outcome == FileOutcome.Deleted);
            File.Exists(RepoFile("entitas-graph")).Should().BeFalse();
        }

        [Fact]
        public void Reinstalling_replaces_the_block_and_keeps_everything_else_byte_for_byte()
        {
            const string before = "# Project\r\n\r\nRules.\r\n";
            const string after = "\r\n## Later section\r\nMore rules.\r\n";
            var stale = $"{InstructionBlock.Begin}\r\nold text\r\n{InstructionBlock.End}";
            File.WriteAllText(RepoFile("CLAUDE.md"), before + stale + after);

            Install().Files.Should().Contain(f => f.Path == RepoFile("CLAUDE.md") && f.Outcome == FileOutcome.Updated);
            Install().Files.Should().Contain(f => f.Path == RepoFile("CLAUDE.md") && f.Outcome == FileOutcome.Unchanged);

            var text = File.ReadAllText(RepoFile("CLAUDE.md"));
            text.Should().StartWith(before).And.EndWith(after).And.NotContain("old text");
            Count(text, InstructionBlock.Begin).Should().Be(1);
            text.Replace("\r\n", "").Should().NotContain("\n", "a CRLF file keeps CRLF line breaks");
        }

        [Fact]
        public void Appends_after_one_blank_line_and_uninstall_restores_the_file()
        {
            const string original = "# Project\r\n\r\nRules.\r\n";
            File.WriteAllText(RepoFile("CLAUDE.md"), original);

            Install();
            var installed = File.ReadAllText(RepoFile("CLAUDE.md"));
            installed.Should().StartWith(original + "\r\n" + InstructionBlock.Begin + "\r\n");
            installed.Should().EndWith(InstructionBlock.End + "\r\n");

            var result = AssistantSetup.Uninstall(_unity, None);

            result.Files.Should().ContainSingle(f => f.Path == RepoFile("CLAUDE.md") && f.Outcome == FileOutcome.Updated);
            File.ReadAllText(RepoFile("CLAUDE.md")).Should().Be(original);
        }

        [Fact]
        public void Appending_to_a_file_without_a_final_line_break_still_leaves_one_blank_line()
        {
            var block = $"{InstructionBlock.Begin}\nx\n{InstructionBlock.End}";
            var updated = InstructionBlock.Install("Rules.", block);

            updated.Should().Be($"Rules.\n\n{block}\n");
            InstructionBlock.Uninstall(updated).Should().Be("Rules.\n");
        }

        [Fact]
        public void Uninstall_keeps_the_paragraphs_around_a_block_in_the_middle_apart()
        {
            var text = $"A\n\n{InstructionBlock.Begin}\nx\n{InstructionBlock.End}\nB\n";

            InstructionBlock.Uninstall(text).Should().Be("A\n\nB\n");
        }

        [Fact]
        public void Uninstall_deletes_a_file_that_held_only_the_block()
        {
            Install();

            var result = AssistantSetup.Uninstall(_unity, None);

            result.Files.Should().ContainSingle(f => f.Path == RepoFile("CLAUDE.md") && f.Outcome == FileOutcome.Deleted);
            File.Exists(RepoFile("CLAUDE.md")).Should().BeFalse();
        }

        [Fact]
        public void Uninstall_without_a_block_changes_nothing()
        {
            File.WriteAllText(RepoFile("CLAUDE.md"), "# Project\n");

            var result = AssistantSetup.Uninstall(_unity, None);

            result.Files.Should().ContainSingle(f => f.Outcome == FileOutcome.Unchanged);
            File.ReadAllText(RepoFile("CLAUDE.md")).Should().Be("# Project\n");
        }

        [Fact]
        public void AGENTS_md_is_updated_only_when_the_project_has_one()
        {
            Install();
            File.Exists(RepoFile("AGENTS.md")).Should().BeFalse();

            File.WriteAllText(RepoFile("AGENTS.md"), "# Agents\n");
            var result = Install();

            result.Files.Should().Contain(f => f.Path == RepoFile("AGENTS.md") && f.Outcome == FileOutcome.Updated);
            File.ReadAllText(RepoFile("AGENTS.md")).Should().StartWith("# Agents\n\n" + InstructionBlock.Begin);

            AssistantSetup.Uninstall(_unity, None);
            File.ReadAllText(RepoFile("AGENTS.md")).Should().Be("# Agents\n");
        }

        [Fact]
        public void A_CLAUDE_md_importing_AGENTS_md_is_left_alone_so_the_block_is_not_loaded_twice()
        {
            File.WriteAllText(RepoFile("CLAUDE.md"), "@AGENTS.md\n");
            File.WriteAllText(RepoFile("AGENTS.md"), "# Agents\n");

            var result = Install();

            File.ReadAllText(RepoFile("CLAUDE.md")).Should().Be("@AGENTS.md\n");
            File.ReadAllText(RepoFile("AGENTS.md")).Should().Contain(InstructionBlock.Begin);
            result.Files.Should().Contain(f => f.Path == RepoFile("CLAUDE.md") && f.Outcome == FileOutcome.Unchanged);
        }

        [Fact]
        public void Without_a_git_root_the_block_goes_to_the_Unity_root_and_gitignore_is_left_alone()
        {
            var unity = Path.Combine(Path.GetTempPath(), "entitas-graph-install-nogit-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path.Combine(unity, "Assets"));
            try
            {
                if (AssistantSetup.FindGitRoot(unity) != null) return; // a repository around the temp folder: nothing to test here

                var result = AssistantSetup.Install(unity, None, gitignore: true, Path.Combine(unity, "Assets", "Tools~", "entitas-graph.dll"));

                File.ReadAllText(Path.Combine(unity, "CLAUDE.md")).Should().Contain("    ./entitas-graph who-reads <Component>");
                File.ReadAllText(Path.Combine(unity, "entitas-graph")).Should().Contain("root=\"$here\"\n").And.Contain("\"$here/Assets/Tools~/entitas-graph.dll\"");
                File.ReadAllText(Path.Combine(unity, "entitas-graph.cmd")).Should().Contain("set \"root=%~dp0.\"\r\n");
                File.Exists(Path.Combine(unity, ".gitignore")).Should().BeFalse();
                result.Notes.Should().Contain(n => n.Contains(".gitignore"));
            }
            finally
            {
                Directory.Delete(unity, recursive: true);
            }
        }

        [Fact]
        public void A_git_file_marks_a_worktree_root()
        {
            var worktree = Path.Combine(_repo, "worktree");
            Directory.CreateDirectory(Path.Combine(worktree, "game", "Assets"));
            File.WriteAllText(Path.Combine(worktree, ".git"), "gitdir: ../.git/worktrees/w\n");

            AssistantSetup.FindGitRoot(Path.Combine(worktree, "game")).Should().Be(worktree);
        }

        [Fact]
        public void Gitignore_gets_the_output_folder_once()
        {
            File.WriteAllText(RepoFile(".gitignore"), "Library/\nTemp/");

            var first = Install();
            Install();

            File.ReadAllText(RepoFile(".gitignore")).Should().Be("Library/\nTemp/\nentitas-graph-out/\n");
            first.Files.Should().Contain(f => f.Path == RepoFile(".gitignore") && f.Outcome == FileOutcome.Updated);
        }

        [Fact]
        public void Gitignore_is_created_when_missing_and_skipped_with_no_gitignore()
        {
            AssistantSetup.Install(_unity, None, gitignore: false, _tool);
            File.Exists(RepoFile(".gitignore")).Should().BeFalse();

            Install();
            File.ReadAllText(RepoFile(".gitignore")).Should().Be("entitas-graph-out/\n");
        }

        [Theory]
        [InlineData("entitas-graph-out/")]
        [InlineData("entitas-graph-out")]
        [InlineData("/src/game/entitas-graph-out/")]
        [InlineData("src/game/entitas-graph-out")]
        [InlineData("**/entitas-graph-out/")]
        [InlineData("entitas-graph-*")]
        [InlineData("*-out/")]
        [InlineData("/src/game/entitas-graph-out/\n# comment")]
        public void Gitignore_is_left_alone_when_a_line_already_covers_the_folder(string existing)
        {
            File.WriteAllText(RepoFile(".gitignore"), existing + "\n");

            var result = Install();

            File.ReadAllText(RepoFile(".gitignore")).Should().Be(existing + "\n");
            result.Files.Should().Contain(f => f.Path == RepoFile(".gitignore") && f.Outcome == FileOutcome.Unchanged);
        }

        [Theory]
        [InlineData("/entitas-graph-out/")]           // anchored to the repository root, the folder is in src/game
        [InlineData("game/entitas-graph-out/")]      // anchored, wrong prefix
        [InlineData("entitas-graph-out/\n!entitas-graph-out/")]
        [InlineData("# entitas-graph-out/")]
        public void Gitignore_lines_that_do_not_cover_the_folder_do_not_count(string existing)
        {
            GitIgnore.Ignores(existing, "src/game/entitas-graph-out").Should().BeFalse();
        }

        [Fact]
        public void A_folder_that_is_not_a_Unity_root_is_refused()
        {
            var act = () => AssistantSetup.Install(_repo, None, gitignore: true, _tool);

            act.Should().Throw<SetupException>().WithMessage("*not a Unity project root*");
            File.Exists(RepoFile("CLAUDE.md")).Should().BeFalse();
            var uninstall = () => AssistantSetup.Uninstall(_repo, None);
            uninstall.Should().Throw<SetupException>();
        }

        [Fact]
        public void A_begin_marker_without_an_end_marker_stops_before_writing_anything()
        {
            File.WriteAllText(RepoFile("AGENTS.md"), "# Agents\n");
            var broken = $"# Project\n{InstructionBlock.Begin}\nhalf a block\n";
            File.WriteAllText(RepoFile("CLAUDE.md"), broken);

            var act = () => Install();

            act.Should().Throw<InvalidOperationException>().WithMessage("*CLAUDE.md*no matching*");
            File.ReadAllText(RepoFile("CLAUDE.md")).Should().Be(broken);
            File.ReadAllText(RepoFile("AGENTS.md")).Should().Be("# Agents\n");
        }

        [Fact]
        public void A_markers_mention_inside_a_line_is_not_a_block()
        {
            var text = $"Install writes `{InstructionBlock.Begin}` …\n";

            InstructionBlock.Uninstall(text).Should().BeNull();
            InstructionBlock.Install(text, "BLOCK").Should().Be(text + "\nBLOCK\n");
        }

        [Fact]
        public void A_byte_order_mark_survives_the_update()
        {
            var bom = new UTF8Encoding(encoderShouldEmitUTF8Identifier: true);
            File.WriteAllText(RepoFile("CLAUDE.md"), "# Project\n", bom);

            Install();

            var bytes = File.ReadAllBytes(RepoFile("CLAUDE.md"));
            bytes.Take(3).Should().Equal(0xEF, 0xBB, 0xBF);
            Count(Encoding.UTF8.GetString(bytes), "﻿").Should().Be(1);
        }

        static int Count(string text, string value)
        {
            var count = 0;
            for (var i = text.IndexOf(value, StringComparison.Ordinal); i >= 0; i = text.IndexOf(value, i + value.Length, StringComparison.Ordinal))
                count++;
            return count;
        }
    }
}
