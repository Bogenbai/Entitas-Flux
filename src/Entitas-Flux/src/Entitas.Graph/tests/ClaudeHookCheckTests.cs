using System.IO;
using System.Text.Json;
using Entitas.Graph.Setup;
using FluentAssertions;
using Xunit;

namespace Entitas.Graph.Tests
{
    /// <summary>
    /// What the Claude Code hook stops: reads of graph.json and graph.html, and nothing that only
    /// names them — a commit message, a PR body, a note written through a heredoc.
    /// </summary>
    public sealed class ClaudeHookCheckTests
    {
        const string Output = "/repo/src/game/entitas-graph-out";

        static string Call(string tool, object input) =>
            JsonSerializer.Serialize(new { session_id = "s", cwd = "/repo", hook_event_name = "PreToolUse", tool_name = tool, tool_input = input });

        static bool Bash(string command) => ClaudeHookCheck.Stops(Call("Bash", new { command, description = "Run it" }));

        [Theory]
        [InlineData(Output + "/graph.json")]
        [InlineData(Output + "/graph.html")]
        public void Reading_either_file_is_stopped(string path) =>
            ClaudeHookCheck.Stops(Call("Read", new { file_path = path })).Should().BeTrue();

        [Fact]
        public void The_report_and_other_files_are_read_freely()
        {
            ClaudeHookCheck.Stops(Call("Read", new { file_path = Output + "/GRAPH_REPORT.md" })).Should().BeFalse();
            ClaudeHookCheck.Stops(Call("Read", new { file_path = "/repo/docs/graph.json" })).Should().BeFalse("another graph.json is not ours");
            ClaudeHookCheck.Stops(Call("Read", new { file_path = "/repo/src/game/Assets/Code/Health.cs" })).Should().BeFalse();
        }

        [Fact]
        public void A_search_there_is_stopped_a_search_for_the_folders_name_is_not()
        {
            ClaudeHookCheck.Stops(Call("Grep", new { pattern = "Health", path = Output })).Should().BeTrue("it goes through graph.json");
            ClaudeHookCheck.Stops(Call("Grep", new { pattern = "Health", path = Output + "/graph.json" })).Should().BeTrue();
            ClaudeHookCheck.Stops(Call("Glob", new { pattern = "src/game/entitas-graph-out/*" })).Should().BeTrue();

            ClaudeHookCheck.Stops(Call("Grep", new { pattern = "Health", path = Output + "/GRAPH_REPORT.md" })).Should().BeFalse();
            ClaudeHookCheck.Stops(Call("Grep", new { pattern = "entitas-graph-out", path = "/repo" })).Should().BeFalse("the folder is ignored by git, so rg never enters it");
        }

        [Theory]
        [InlineData("cat src/game/entitas-graph-out/graph.json | head")]
        [InlineData("head -c 4000 src/game/entitas-graph-out/graph.html")]
        [InlineData("jq '.nodes | length' entitas-graph-out/graph.json")]
        [InlineData("python3 -c \"import json; print(len(json.load(open('entitas-graph-out/graph.json'))['nodes']))\"")]
        [InlineData("python3 - <<'EOF'\nimport json\nprint(json.load(open('src/game/entitas-graph-out/graph.json')).keys())\nEOF")]
        [InlineData("python3 tally.py < entitas-graph-out/graph.json")]
        [InlineData("cd src/game/entitas-graph-out && cat graph.json")]
        [InlineData("cat entitas-graph-out/*")]
        [InlineData("echo \"$(cat entitas-graph-out/graph.json)\"")]
        [InlineData("grep -rn Health src/game/entitas-graph-out")]
        [InlineData("rg Health entitas-graph-out/")]
        [InlineData("FOO=1 sudo cat entitas-graph-out/graph.json")]
        public void Commands_that_read_a_graph_file_are_stopped(string command) => Bash(command).Should().BeTrue();

        [Theory]
        // The report from a real session: the commit message named the folder and the file.
        [InlineData("git commit -m \"Install the graph: entitas-graph-out/ is ignored, graph.json stays out of git\"")]
        [InlineData("git add .gitignore && git commit -m \"$(cat <<'EOF'\nIgnore entitas-graph-out/graph.json\n\nCo-Authored-By: someone\nEOF\n)\"")]
        [InlineData("gh pr create --title \"Graph\" --body \"$(cat <<'EOF'\n## Summary\n- \"Generate\" keeps (entitas-graph-out/graph.json) out of reach\n- see graph.html\nEOF\n)\"")]
        [InlineData("gh issue comment 5 --body \"graph.json in entitas-graph-out/ is rebuilt by every query\"")]
        [InlineData("cat > NOTES.md <<'EOF'\nThe graph lives in entitas-graph-out/graph.json.\nEOF")]
        [InlineData("echo \"graph.json lives in entitas-graph-out\" >> NOTES.md")]
        [InlineData("ls -la src/game/entitas-graph-out/")]
        [InlineData("du -sh entitas-graph-out/graph.json")]
        [InlineData("open src/game/entitas-graph-out/graph.html")]
        [InlineData("cat src/game/entitas-graph-out/GRAPH_REPORT.md")]
        [InlineData("grep -n \"graph.json\" src/game/entitas-graph-out/GRAPH_REPORT.md")]
        [InlineData("grep -rn entitas-graph-out .gitignore CLAUDE.md")]
        [InlineData("./entitas-graph diff src/game/entitas-graph-out/graph.json old.json")]
        [InlineData("dotnet src/game/Assets/Entitas/Entitas/Tools~/entitas-graph.dll build -o entitas-graph-out")]
        [InlineData("git status")]
        public void Commands_that_only_name_them_pass(string command) => Bash(command).Should().BeFalse();

        [Theory]
        [InlineData("cat \"entitas-graph-out/graph.json", true)]
        [InlineData("echo $(cat entitas-graph-out/graph.json", true)]
        [InlineData("echo `cat entitas-graph-out/graph.json", true)]
        [InlineData("$(\"$(\\", false)]
        [InlineData("echo \"$(cat entitas-graph-out/graph.json \\", true)]
        [InlineData("cat <<EOF\nentitas-graph-out/graph.json", false)]
        [InlineData(")))) cat entitas-graph-out/graph.json", true)]
        [InlineData("<<", false)]
        [InlineData("'", false)]
        public void A_command_cut_off_anywhere_is_still_read_to_the_end(string command, bool stops) => Bash(command).Should().Be(stops);

        [Fact]
        public void A_stopped_call_exits_2_with_what_to_ask_instead_and_a_call_it_cannot_read_passes()
        {
            var error = new StringWriter();
            ClaudeHookCheck.Run(new StringReader(Call("Read", new { file_path = Output + "/graph.json" })), error).Should().Be(2);
            error.ToString().Should().Contain("./entitas-graph who-reads <Component>");

            error = new StringWriter();
            ClaudeHookCheck.Run(new StringReader("not json, entitas-graph-out/graph.json"), error).Should().Be(0);
            error.ToString().Should().BeEmpty();
        }
    }
}
