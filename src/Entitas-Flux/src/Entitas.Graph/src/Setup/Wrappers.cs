using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace Entitas.Graph.Setup
{
    /// <summary>
    /// The two short commands <c>entitas-graph install</c> puts next to the instruction file:
    /// <c>entitas-graph</c> (sh, for macOS, Linux and Git Bash) and <c>entitas-graph.cmd</c> (cmd
    /// and PowerShell, which may refuse unsigned .ps1 scripts). Each finds entitas-graph.dll when
    /// it runs — in Assets/…/Tools~ for a manual install, under Library/PackageCache for a UPM one,
    /// whatever hash the package folder has this time — and points the tool at the Unity root
    /// through ENTITAS_GRAPH_ROOT, so an explicit -r still wins. The block then names
    /// <c>./entitas-graph</c> instead of a path that broke on every package update and never
    /// expanded its glob in cmd or PowerShell. Strings in, strings out.
    /// </summary>
    public static class Wrappers
    {
        public const string ShName = "entitas-graph";
        public const string CmdName = "entitas-graph.cmd";

        /// <summary>The environment variable the wrappers set to the Unity root; the CLI's default for -r.</summary>
        public const string RootVariable = "ENTITAS_GRAPH_ROOT";

        /// <summary>What marks a file as one install wrote: only such files are rewritten or deleted.</summary>
        public const string Signature = "Written by `entitas-graph install`";

        public static bool IsOurs(string text) => text.Contains(Signature);

        /// <param name="directory">Where the wrappers go: the repository root.</param>
        /// <param name="toolPath">The entitas-graph.dll install ran from, tried first.</param>
        public static string Sh(string directory, string unityRoot, string toolPath, string repositoryRoot)
        {
            var root = InstructionBlock.RelativePath(directory, unityRoot);
            var lines = new List<string>
            {
                "#!/bin/sh",
                $"# {Signature}: runs the ECS graph of the Unity project in {(root == "." ? "this folder" : root)}.",
                "# Usage: ./entitas-graph who-reads <Component>   (./entitas-graph --help for every command)",
                "# Install rewrites this file and uninstall removes it; edits here are lost.",
                "",
                // A Claude Code PreToolUse hook (.claude/settings.json) runs this on every Read, Grep,
                // Glob and Bash call: plain sh on the call's JSON, so no dotnet start and no jq.
                "# --claude-hook: the Claude Code hook install puts in .claude/settings.json. It reads the tool call",
                "# from stdin and stops one that reads graph.json (~1.4 M tokens, for programs) or graph.html (for a browser).",
                $"if [ \"$1\" = \"{ClaudeHook.Flag}\" ]; then",
                "  call=$(cat)",
                "  case \"$call\" in",
                "    *entitas-graph-out*) ;;",
                "    *) exit 0 ;;",
                "  esac",
                "  case \"$call\" in",
                "    */entitas-graph\\ *|*entitas-graph.dll*|*entitas-graph.cmd*) exit 0 ;;   # the tool itself reads it",
                "  esac",
                "  case \"$call\" in",
                "    *graph.json*|*graph.html*|*'\"tool_name\":\"Grep\"'*|*'\"tool_name\": \"Grep\"'*|*'\"tool_name\":\"Glob\"'*|*'\"tool_name\": \"Glob\"'*)",
                $"      echo \"{ClaudeHook.Message}\" >&2",
                "      exit 2 ;;",
                "  esac",
                "  exit 0",
                "fi",
                "",
                "here=$(cd \"$(dirname \"$0\")\" && pwd)",
                "root=" + (root == "." ? "\"$here\"" : "\"$here/" + ShEscape(root) + "\""),
                "for tool in " + string.Join(" ", ShCandidates(directory, unityRoot, toolPath, repositoryRoot)) + "; do",
                $"  [ -f \"$tool\" ] && {RootVariable}=\"$root\" exec dotnet \"$tool\" \"$@\"",
                "done",
                "echo \"entitas-graph: entitas-graph.dll not found in $root (Assets/…/Tools~, Library/PackageCache or Packages) — is the Entitas-Flux package installed?\" >&2",
                "exit 1",
            };
            return string.Join("\n", lines) + "\n";
        }

        /// <summary>The same for cmd and PowerShell, with CRLF line breaks.</summary>
        public static string Cmd(string directory, string unityRoot, string toolPath, string repositoryRoot)
        {
            var root = InstructionBlock.RelativePath(directory, unityRoot);
            var lines = new List<string>
            {
                "@echo off",
                $"rem {Signature}: runs the ECS graph of the Unity project in {(root == "." ? "this folder" : root)}.",
                "rem Usage: entitas-graph.cmd who-reads <Component>   (entitas-graph.cmd --help for every command)",
                "rem Install rewrites this file and uninstall removes it; edits here are lost.",
                "setlocal",
                // %~dp0 ends with a backslash, which would escape the closing quote of "-r C:\repo\";
                // "." keeps the folder without one.
                "set \"root=%~dp0" + (root == "." ? "." : CmdEscape(root.Replace('/', '\\'))) + "\"",
                "set \"tool=\"",
            };
            var recorded = Recorded(directory, unityRoot, toolPath, repositoryRoot);
            if (recorded != null)
                lines.Add($"if exist \"{recorded.Cmd}\" set \"tool={recorded.Cmd}\"");
            foreach (var folder in new[] { "Library\\PackageCache", "Packages" })
                lines.Add($"if not defined tool for /d %%p in (\"%root%\\{folder}\\*\") do if exist \"%%p\\Tools~\\entitas-graph.dll\" set \"tool=%%p\\Tools~\\entitas-graph.dll\"");
            lines.Add("if not defined tool if exist \"%root%\\Assets\\Entitas\\Entitas\\Tools~\\entitas-graph.dll\" set \"tool=%root%\\Assets\\Entitas\\Entitas\\Tools~\\entitas-graph.dll\"");
            // No ( … ) block: a root such as "Program Files (x86)" would end it early.
            lines.Add("if not defined tool goto missing");
            lines.Add($"set \"{RootVariable}=%root%\"");
            lines.Add("dotnet \"%tool%\" %*");
            lines.Add("exit /b %ERRORLEVEL%");
            lines.Add(":missing");
            lines.Add("echo entitas-graph: entitas-graph.dll not found in %root% (Assets\\...\\Tools~, Library\\PackageCache or Packages) - is the Entitas-Flux package installed? 1>&2");
            lines.Add("exit /b 1");
            return string.Join("\r\n", lines) + "\r\n";
        }

        /// <summary>
        /// Where the sh wrapper looks, in order: the tool install ran from, then any package in
        /// Library/PackageCache or Packages, then the manual layout — so switching between a
        /// manual and a UPM install, or updating the package, needs no new install.
        /// </summary>
        static IEnumerable<string> ShCandidates(string directory, string unityRoot, string toolPath, string repositoryRoot)
        {
            var recorded = Recorded(directory, unityRoot, toolPath, repositoryRoot);
            if (recorded != null)
                yield return recorded.Sh;
            yield return "\"$root\"/Library/PackageCache/*/Tools~/entitas-graph.dll";
            yield return "\"$root\"/Packages/*/Tools~/entitas-graph.dll";
            yield return "\"$root/Assets/Entitas/Entitas/Tools~/entitas-graph.dll\"";
        }

        sealed class Located
        {
            public string Sh = string.Empty;
            public string Cmd = string.Empty;
        }

        /// <summary>
        /// The tool install ran from, as each script names it: relative to the wrapper inside the
        /// repository, absolute outside it (a build of the framework itself, this machine only).
        /// Null under Library/PackageCache, whose folder name changes with every update — the
        /// glob that follows covers it — and for the manual layout, which is tried last anyway.
        /// </summary>
        static Located? Recorded(string directory, string unityRoot, string toolPath, string repositoryRoot)
        {
            if (InstructionBlock.IsUnder(toolPath, Path.Combine(unityRoot, "Library", "PackageCache")))
                return null;
            if (InstructionBlock.IsUnder(toolPath, Path.Combine(unityRoot, "Assets", "Entitas", "Entitas", "Tools~")))
                return null;
            if (!InstructionBlock.IsUnder(toolPath, repositoryRoot))
            {
                var full = Path.GetFullPath(toolPath);
                return new Located { Sh = "\"" + ShEscape(full.Replace('\\', '/')) + "\"", Cmd = CmdEscape(full.Replace('/', '\\')) };
            }

            var relative = InstructionBlock.RelativePath(directory, toolPath);
            return new Located { Sh = "\"$here/" + ShEscape(relative) + "\"", Cmd = "%~dp0" + CmdEscape(relative.Replace('/', '\\')) };
        }

        /// <summary>Text inside sh double quotes: $, `, " and \ escaped.</summary>
        static string ShEscape(string text)
        {
            var sb = new StringBuilder();
            foreach (var c in text)
            {
                if (c is '$' or '`' or '"' or '\\') sb.Append('\\');
                sb.Append(c);
            }
            return sb.ToString();
        }

        /// <summary>Text inside a cmd "set" or path: % doubled.</summary>
        static string CmdEscape(string text) => text.Replace("%", "%%");

        /// <summary>Both wrappers' file names in <paramref name="directory"/>, sh first.</summary>
        public static IEnumerable<string> Files(string directory) =>
            new[] { ShName, CmdName }.Select(name => Path.Combine(directory, name));
    }
}
