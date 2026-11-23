using System;
using System.Text;
using System.Text.RegularExpressions;

namespace Entitas.Graph.Setup
{
    /// <summary>
    /// Just enough of .gitignore to tell whether entitas-graph-out/ is already ignored, so install
    /// adds its line only when needed and never touches the lines that are there.
    /// </summary>
    public static class GitIgnore
    {
        /// <summary>
        /// No leading slash: in the git root's .gitignore it ignores the folder at any depth,
        /// which is where it is when the Unity root is a subfolder of the repository.
        /// </summary>
        public const string OutputLine = "entitas-graph-out/";

        /// <summary>
        /// Whether the patterns ignore <paramref name="folder"/> (relative to the .gitignore's
        /// folder, '/'-separated): globs, anchoring, and negation with the last match winning — as
        /// git reads one file. Other .gitignore files and global excludes are not consulted; a
        /// line in this file is what the whole team gets.
        /// </summary>
        public static bool Ignores(string content, string folder)
        {
            var segments = folder.Replace('\\', '/').Trim('/').Split('/');
            var ignored = false;
            foreach (var raw in content.Split('\n'))
            {
                var pattern = raw.TrimEnd('\r', ' ');
                if (pattern.Length == 0 || pattern[0] == '#') continue;

                var negated = pattern[0] == '!';
                if (negated) pattern = pattern.Substring(1);
                pattern = pattern.TrimEnd('/');
                if (pattern.Length == 0) continue;

                // A slash anywhere but at the end anchors the pattern to the .gitignore's folder;
                // without one it matches a name at any depth.
                var anchored = pattern.Contains('/');
                var regex = new Regex("^" + ToRegex(pattern.TrimStart('/')) + "$");
                for (var depth = 1; depth <= segments.Length; depth++)
                {
                    // Every enclosing folder counts too: ignoring a parent ignores what is in it.
                    var candidate = anchored ? string.Join("/", segments, 0, depth) : segments[depth - 1];
                    if (regex.IsMatch(candidate))
                    {
                        ignored = !negated;
                        break;
                    }
                }
            }

            return ignored;
        }

        /// <summary>The content with <paramref name="line"/> appended on a line of its own, in the file's line breaks.</summary>
        public static string Append(string? content, string line)
        {
            if (string.IsNullOrEmpty(content)) return line + "\n";
            var newline = content.Contains("\r\n") ? "\r\n" : "\n";
            return content + (content.EndsWith("\n", StringComparison.Ordinal) ? string.Empty : newline) + line + newline;
        }

        static string ToRegex(string glob)
        {
            var sb = new StringBuilder();
            for (var i = 0; i < glob.Length; i++)
            {
                var c = glob[i];
                switch (c)
                {
                    case '*' when i + 1 < glob.Length && glob[i + 1] == '*':
                        // "**/" matches zero or more folders, a trailing "/**" everything inside.
                        if ((i == 0 || glob[i - 1] == '/') && i + 2 < glob.Length && glob[i + 2] == '/')
                        {
                            sb.Append("(?:.*/)?");
                            i += 2;
                        }
                        else
                        {
                            sb.Append(".*");
                            i++;
                        }
                        break;
                    case '*':
                        sb.Append("[^/]*");
                        break;
                    case '?':
                        sb.Append("[^/]");
                        break;
                    case '[' when glob.IndexOf(']', i + 1) > i + 1:
                        var close = glob.IndexOf(']', i + 1);
                        var set = glob.Substring(i + 1, close - i - 1);
                        sb.Append('[').Append(set[0] == '!' ? "^" + set.Substring(1) : set).Append(']');
                        i = close;
                        break;
                    case '\\' when i + 1 < glob.Length:
                        sb.Append(Regex.Escape(glob[++i].ToString()));
                        break;
                    default:
                        sb.Append(Regex.Escape(c.ToString()));
                        break;
                }
            }

            return sb.ToString();
        }
    }
}
