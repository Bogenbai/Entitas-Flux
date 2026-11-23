using System;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;

namespace Entitas.Graph.Setup
{
    /// <summary>
    /// The text side of <c>entitas-graph install</c>: the block that tells an AI assistant to ask
    /// the graph instead of grepping, and putting it into or taking it out of an instruction file
    /// (CLAUDE.md, AGENTS.md). Strings in, strings out — reading and writing files is
    /// <see cref="AssistantSetup"/>'s business.
    /// </summary>
    public static class InstructionBlock
    {
        public const string Begin = "<!-- entitas-graph:begin -->";
        public const string End = "<!-- entitas-graph:end -->";

        /// <summary>
        /// The block, with <c>\n</c> line breaks. It is loaded into every assistant session, so it
        /// stays short. Every path in it is relative to <paramref name="targetDirectory"/>, the
        /// folder of the instruction file, because that is where the assistant runs commands
        /// from — usually the repository root, which is often above the Unity root.
        /// </summary>
        /// <param name="toolPath">Where entitas-graph.dll lives.</param>
        /// <param name="repositoryRoot">The git root, or the Unity root without one — where the <see cref="Wrappers"/> are.</param>
        public static string Build(string targetDirectory, string unityRoot, string toolPath, string repositoryRoot)
        {
            // The wrappers find the tool and the Unity root themselves, so the block names a short
            // command that survives package updates instead of `dotnet <path> … -r <root>`.
            var wrappers = RelativePath(targetDirectory, repositoryRoot);
            var sh = wrappers == "." ? "./" + Wrappers.ShName : Quote(wrappers + "/" + Wrappers.ShName);
            var cmd = (wrappers == "." ? "." : wrappers.Replace('/', '\\')) + "\\" + Wrappers.CmdName;
            var root = RelativePath(targetDirectory, unityRoot);
            var report = (root == "." ? string.Empty : root + "/") + "entitas-graph-out/GRAPH_REPORT.md";
            return string.Join("\n",
                Begin,
                "## ECS data flow",
                "This is an Entitas project. To find who creates, reads, filters, removes or destroys a component, what a feature",
                "runs and in what order, or how data gets from one system to another, run entitas-graph from this file's folder instead of searching the code:",
                "",
                $"    {sh} who-reads <Component>        (Windows cmd or PowerShell: {cmd}; --help lists every command and option)",
                "",
                "Commands: who-creates, who-reads, lifecycle <Component>; systems-of <Feature>; path <A> <B>;",
                "explain <Name>; query \"<words>\" (the connected subgraph around the matching nodes);",
                "flow <Request|System> (what happens after it: the systems it reaches, each in which frame);",
                "diff --commit <sha> (what one commit changed); diff --ref <branch> (changes since a branch, for reviewing it);",
                $"report (= `{report}`, an overview worth reading once). Each answer is capped at ~2000 tokens. Never read graph.json (~1.4 M tokens).",
                // Subagents whose tools: list has no Bash cannot run the command; the MCP server
                // install registers gives them the same answers.
                $"Subagents without Bash: list mcp__{McpConfig.Server}__* in their tools: (the same commands as MCP tools), or put your answer in their prompt.",
                // The graph is the map, the code the ground: an answer says where to look, and what it
                // infers (links through calls, frames) or cannot see (DI, scenes) is checked there.
                "Before changing code, open the files an answer names: links marked * and frames are inferred, and DI, scenes and prefabs are not in the graph.",
                End);
        }

        /// <summary>
        /// The file with the block in it: an existing block is replaced in place, anything else in
        /// the file is kept byte for byte, and the block takes the file's line breaks (CRLF stays
        /// CRLF). Without a block, it is appended after one blank line. Returns the input itself
        /// when nothing changes.
        /// </summary>
        /// <exception cref="FormatException">A begin marker without an end marker.</exception>
        public static string Install(string? existing, string block)
        {
            if (string.IsNullOrEmpty(existing)) return block + "\n";

            var newline = NewlineOf(existing);
            block = block.Replace("\n", newline);
            var span = Find(existing);
            if (span != null)
            {
                var (start, end) = span.Value;
                var replaced = existing.Substring(0, start) + block + existing.Substring(end);
                return replaced == existing ? existing : replaced;
            }

            var separator = EndsWithBlankLine(existing) ? string.Empty
                : existing.EndsWith("\n", StringComparison.Ordinal) ? newline
                : newline + newline;
            return existing + separator + block + newline;
        }

        /// <summary>
        /// The file without the block — and without the blank line <see cref="Install"/> put
        /// before it — or null when it has no block. An empty or whitespace-only result means
        /// nothing else was in the file.
        /// </summary>
        /// <exception cref="FormatException">A begin marker without an end marker.</exception>
        public static string? Uninstall(string existing)
        {
            var span = Find(existing);
            if (span == null) return null;

            var text = existing;
            for (; span != null; span = Find(text))
            {
                var (start, end) = span.Value;
                end += LeadingLineBreak(text, end);
                var before = text.Substring(0, start);
                var after = text.Substring(end);
                // Only when the blank line separates the block from nothing below (or from a
                // paragraph that has its own blank line), so two paragraphs never get glued.
                if (EndsWithBlankLine(before) && (after.Length == 0 || LeadingLineBreak(after, 0) > 0))
                    before = before.Substring(0, before.Length - TrailingLineBreak(before));
                text = before + after;
            }

            return text;
        }

        /// <summary>
        /// Whether a CLAUDE.md pulls AGENTS.md in through Claude Code's <c>@file</c> import: the
        /// block in AGENTS.md then reaches the assistant already, and a second copy would only
        /// cost tokens in every session.
        /// </summary>
        public static bool Imports(string content, string fileName) =>
            Regex.IsMatch(content, @"(^|\s)@(\./)?" + Regex.Escape(fileName) + @"(?=\s|$)", RegexOptions.Multiline);

        public static string RelativePath(string from, string to) => Path.GetRelativePath(from, to).Replace('\\', '/');

        public static bool IsUnder(string path, string directory)
        {
            var relative = RelativePath(directory, path);
            return relative != ".." && !relative.StartsWith("../", StringComparison.Ordinal) && !Path.IsPathRooted(relative);
        }

        /// <summary>
        /// A path as a shell word: segments that need it (a space in "My Game") are quoted one by
        /// one, so a glob segment next to them still expands.
        /// </summary>
        static string Quote(string path) => string.Join("/", path.Split('/').Select(QuoteSegment));

        static string QuoteSegment(string segment) =>
            segment.All(c => char.IsLetterOrDigit(c) || "._-+@~*:".IndexOf(c) >= 0) && !segment.StartsWith("~", StringComparison.Ordinal)
                ? segment
                : "\"" + segment + "\"";

        /// <summary>The block's span, from the begin marker to the end of the end marker, both at the start of a line.</summary>
        static (int Start, int End)? Find(string text)
        {
            var start = IndexAtLineStart(text, Begin, 0);
            if (start < 0) return null;
            var end = IndexAtLineStart(text, End, start + Begin.Length);
            if (end < 0) throw new FormatException($"{Begin} has no matching {End} after it — fix the file by hand.");
            return (start, end + End.Length);
        }

        static int IndexAtLineStart(string text, string marker, int from)
        {
            for (var i = text.IndexOf(marker, from, StringComparison.Ordinal); i >= 0; i = text.IndexOf(marker, i + 1, StringComparison.Ordinal))
                if (i == 0 || text[i - 1] == '\n')
                    return i;
            return -1;
        }

        static string NewlineOf(string text) => text.Contains("\r\n") ? "\r\n" : "\n";

        static bool EndsWithBlankLine(string text) =>
            text.EndsWith("\n\n", StringComparison.Ordinal) || text.EndsWith("\n\r\n", StringComparison.Ordinal);

        static int LeadingLineBreak(string text, int at) =>
            at + 1 < text.Length && text[at] == '\r' && text[at + 1] == '\n' ? 2
            : at < text.Length && text[at] == '\n' ? 1
            : 0;

        static int TrailingLineBreak(string text) =>
            text.EndsWith("\r\n", StringComparison.Ordinal) ? 2 : text.EndsWith("\n", StringComparison.Ordinal) ? 1 : 0;
    }
}
