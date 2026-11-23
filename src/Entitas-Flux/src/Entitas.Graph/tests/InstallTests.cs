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
            text.Should().Contain("    dotnet src/game/Assets/Entitas/Entitas/Tools~/entitas-graph.dll who-reads <Component> -r src/game\n");
            text.Should().Contain("`src/game/entitas-graph-out/GRAPH_REPORT.md`");
            File.Exists(Path.Combine(_unity, "CLAUDE.md")).Should().BeFalse();
            result.Notes.Should().BeEmpty();
        }

        [Fact]
        public void The_block_is_short_and_names_every_command_and_the_cap()
        {
            var block = InstructionBlock.Build(_repo, _unity, _tool, _repo);
            var lines = block.Split('\n');

            lines.Length.Should().BeLessOrEqualTo(15);
            foreach (var command in new[] { "who-creates", "who-reads", "lifecycle", "systems-of", "path", "explain", "query \"<words>\"", "diff --ref", "report" })
                block.Should().Contain(command);
            block.Should().Contain("~2000 tokens").And.Contain("Never read graph.json");
        }

        [Fact]
        public void Omits_the_root_option_when_the_instruction_file_sits_in_the_Unity_root()
        {
            var block = InstructionBlock.Build(_unity, _unity, _tool, _repo);

            block.Should().Contain("    dotnet Assets/Entitas/Entitas/Tools~/entitas-graph.dll who-reads <Component>\n");
            block.Should().NotContain(" -r ");
            block.Should().Contain("`entitas-graph-out/GRAPH_REPORT.md`");
        }

        [Fact]
        public void Names_a_package_cache_install_by_glob_because_its_folder_carries_a_hash()
        {
            var tool = Path.Combine(_unity, "Library", "PackageCache", "com.bogenbai.entitas-flux@1a2b3c4d5e", "Tools~", "entitas-graph.dll");

            AssistantSetup.Install(_unity, None, gitignore: false, tool);

            File.ReadAllText(RepoFile("CLAUDE.md")).Should().Contain(
                "    dotnet src/game/Library/PackageCache/com.bogenbai.entitas-flux*/Tools~/entitas-graph.dll who-reads <Component> -r src/game\n");
        }

        [Fact]
        public void Paths_in_an_explicit_target_are_relative_to_that_file()
        {
            var target = Path.Combine(_repo, "docs", "AI.md");

            Install(target);

            File.ReadAllText(target).Should().Contain("dotnet ../src/game/Assets/Entitas/Entitas/Tools~/entitas-graph.dll who-reads <Component> -r ../src/game\n");
            File.Exists(RepoFile("CLAUDE.md")).Should().BeFalse();
        }

        [Fact]
        public void Quotes_path_segments_with_spaces_but_keeps_the_glob_expandable()
        {
            var unity = Path.Combine(_repo, "My Game");
            Directory.CreateDirectory(Path.Combine(unity, "Assets"));
            var tool = Path.Combine(unity, "Library", "PackageCache", "com.bogenbai.entitas-flux@abc", "Tools~", "entitas-graph.dll");

            var block = InstructionBlock.Build(_repo, unity, tool, _repo);

            block.Should().Contain("dotnet \"My Game\"/Library/PackageCache/com.bogenbai.entitas-flux*/Tools~/entitas-graph.dll who-reads <Component> -r \"My Game\"\n");
        }

        [Fact]
        public void A_tool_outside_the_repository_is_named_by_absolute_path_with_a_note()
        {
            var tool = Path.Combine(Path.GetTempPath(), "elsewhere", "entitas-graph.dll");

            var result = AssistantSetup.Install(_unity, None, gitignore: false, tool);

            File.ReadAllText(RepoFile("CLAUDE.md")).Should().Contain($"dotnet {Path.GetFullPath(tool).Replace('\\', '/')} who-reads");
            result.Notes.Should().ContainSingle(n => n.Contains("absolute path"));
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

                File.ReadAllText(Path.Combine(unity, "CLAUDE.md")).Should().Contain("dotnet Assets/Tools~/entitas-graph.dll who-reads <Component>\n");
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
