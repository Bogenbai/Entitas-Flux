using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace Entitas.Graph.Setup
{
    /// <summary>
    /// The line endings of the two wrappers, pinned in the git root's .gitattributes: the sh one
    /// runs under sh on every OS — the Claude Code hook and the MCP server go through it on Windows
    /// too — and sh stops at the first carriage return, which Git for Windows puts into every text
    /// file it checks out unless told otherwise. cmd, in turn, wants CRLF. Strings in, strings out;
    /// lines already there are never touched.
    /// </summary>
    public static class GitAttributes
    {
        public static IReadOnlyList<string> Lines { get; } = new[]
        {
            "/" + Wrappers.ShName + " text eol=lf",
            "/" + Wrappers.CmdName + " text eol=crlf",
        };

        /// <summary>The content with the lines it lacks appended, or null when it has them all.</summary>
        public static string? Add(string? content)
        {
            var present = new HashSet<string>((content ?? string.Empty).Split('\n').Select(Normalize));
            var missing = Lines.Where(line => !present.Contains(Normalize(line))).ToList();
            if (missing.Count == 0) return null;
            return missing.Aggregate(content, (text, line) => GitIgnore.Append(text, line))!;
        }

        static string Normalize(string line) => Regex.Replace(line.Trim(), @"\s+", " ");
    }
}
