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
        /// <param name="repositoryRoot">The git root, or the Unity root without one.</param>
        public static string Build(string targetDirectory, string unityRoot, string toolPath, string repositoryRoot)
        {
            var tool = CommandPath(targetDirectory, unityRoot, toolPath, repositoryRoot);
            var root = RelativePath(targetDirectory, unityRoot);
            var rootOption = root == "." ? string.Empty : " -r " + Quote(root);
            var report = (root == "." ? string.Empty : root + "/") + "entitas-graph-out/GRAPH_REPORT.md";
            return string.Join("\n",
                Begin,
                "## ECS data flow",
                "This is an Entitas project. To find who creates, reads, filters, removes or destroys a",
                "component, what a feature runs and in what order, or how data gets from one system to",
                "another, run entitas-graph from this file's folder instead of searching the code:",
                "",
                $"    dotnet {tool} who-reads <Component>{rootOption}",
                "",
                "Commands: who-creates, who-reads, lifecycle <Component>; systems-of <Feature>; path <A> <B>;",
                "explain <Name>; query \"<words>\" (the connected subgraph around the matching nodes);",
                "flow <Request|System> (what happens after it: the systems it reaches, each in which frame);",
                "diff --commit <sha> (what one commit changed); diff --ref <branch> (changes since a branch, for reviewing it);",
                $"report (= `{report}`, an overview worth reading once).",
                "Each answer is capped at ~2000 tokens. Never read graph.json: ~1.4 M tokens on a 2000-file project, it is for programs.",
                End);
        }

        /// <summary>
        /// How the block names entitas-graph.dll. Under Library/PackageCache the package folder is
        /// <c>name@hash</c> and the hash changes with every package update, so that folder is
        /// named by glob; a block with the hash would break on the next update. A tool outside the
        /// repository (a build of the framework itself) is named by absolute path: a relative path
        /// climbing out of the repository works on this machine only anyway, and hides that.
        /// </summary>
        public static string CommandPath(string targetDirectory, string unityRoot, string toolPath, string repositoryRoot)
        {
            var packageCache = Path.Combine(unityRoot, "Library", "PackageCache");
            if (IsUnder(toolPath, packageCache))
            {
                var segments = RelativePath(packageCache, toolPath).Split('/');
                var at = segments[0].IndexOf('@');
                segments[0] = (at > 0 ? segments[0].Substring(0, at) : segments[0]) + "*";
                var cache = RelativePath(targetDirectory, packageCache);
                return Quote(cache) + "/" + string.Join("/", segments.Select(QuoteSegment));
            }

            return IsUnder(toolPath, repositoryRoot)
                ? Quote(RelativePath(targetDirectory, toolPath))
                : Quote(Path.GetFullPath(toolPath).Replace('\\', '/'));
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
