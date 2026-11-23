using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Entitas.Graph.Setup
{
    /// <summary>
    /// What the Claude Code hook decides: whether a Read, Grep, Glob or Bash call reads graph.json
    /// or graph.html. The sh wrapper lets every call that never mentions entitas-graph-out through
    /// without starting dotnet; the rest come here. A Bash command is read word by word, so a commit
    /// message, a PR body or a heredoc written to a file can name the files without being stopped —
    /// matching the call's text did, and blocked a <c>git commit -m</c> about this very folder.
    /// Wrong in either direction is cheap, but a stopped legitimate command costs the assistant a
    /// detour, so a word inside quotes with spaces in it counts as text, not a path, except for
    /// an interpreter (python, jq, node…), whose quoted code may well open the file.
    /// </summary>
    public static class ClaudeHookCheck
    {
        public const string Folder = "entitas-graph-out";

        static readonly string[] GraphFiles = { "graph.json", "graph.html" };

        /// <summary>Commands that never print a file's content.</summary>
        static readonly HashSet<string> Harmless = new HashSet<string>(StringComparer.Ordinal)
        {
            "ls", "stat", "du", "file", "wc", "rm", "touch", "mkdir", "test", "[", "git", "echo", "printf", "cd",
            "open", "xdg-open", "start",
        };

        /// <summary>Commands whose quoted arguments are code rather than text.</summary>
        static readonly string[] Interpreters =
        {
            "python", "node", "nodejs", "deno", "bun", "ruby", "perl", "php", "jq", "yq", "awk", "gawk", "mawk", "nawk",
            "sed", "sh", "bash", "zsh", "dash", "ksh", "fish", "pwsh", "powershell", "osascript", "lua", "Rscript", "dotnet",
        };

        /// <summary>Commands that search what they are given; the folder itself as an argument means its graph.json.</summary>
        static readonly HashSet<string> Searchers = new HashSet<string>(StringComparer.Ordinal) { "grep", "egrep", "fgrep", "rg", "ag", "ack" };

        static readonly HashSet<string> Prefixes = new HashSet<string>(StringComparer.Ordinal) { "env", "sudo", "time", "command", "nice", "nohup", "exec", "builtin", "!" };

        /// <summary>Reads the call from <paramref name="input"/>: 2 with the message on <paramref name="error"/> stops it, 0 lets it through.</summary>
        public static int Run(TextReader input, TextWriter error)
        {
            try
            {
                if (!Stops(input.ReadToEnd())) return 0;
                error.WriteLine(ClaudeHook.Message);
                return 2;
            }
            catch (Exception)
            {
                return 0; // a call the hook cannot read is never stopped
            }
        }

        /// <summary>Whether the PreToolUse call (Claude Code's JSON) reads graph.json or graph.html.</summary>
        public static bool Stops(string call)
        {
            using var document = JsonDocument.Parse(call);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("tool_input", out var input) || input.ValueKind != JsonValueKind.Object)
                return false;

            return Text(root, "tool_name") switch
            {
                "Read" => IsGraphFile(Text(input, "file_path")),
                "Grep" => SearchesFolder(Text(input, "path")),
                "Glob" => InFolder(Text(input, "path")) || InFolder(Text(input, "pattern")),
                "Bash" => CommandReads(Text(input, "command") ?? string.Empty),
                _ => false,
            };
        }

        static string? Text(JsonElement element, string name) =>
            element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

        static string[] Segments(string path) => path.Split(new[] { '/', '\\' }, StringSplitOptions.RemoveEmptyEntries);

        static bool InFolder(string? path) => path != null && Segments(path).Contains(Folder);

        static bool IsGraphFile(string? path)
        {
            if (path == null) return false;
            var segments = Segments(path);
            return segments.Length > 1 && segments.Take(segments.Length - 1).Contains(Folder) && GraphFiles.Contains(segments[^1]);
        }

        /// <summary>A search of the folder, or of one of the two files; GRAPH_REPORT.md is meant to be read.</summary>
        static bool SearchesFolder(string? path)
        {
            if (!InFolder(path)) return false;
            var last = Segments(path!)[^1];
            return last == Folder || GraphFiles.Contains(last);
        }

        static bool CommandReads(string command)
        {
            if (!command.Contains(Folder)) return false;

            var insideFolder = false;
            foreach (var simple in ShellWords.Commands(command))
            {
                var words = simple.Words;
                var verbAt = VerbIndex(words);
                if (verbAt < 0) continue;

                var verb = Name(words[verbAt].Text);
                var arguments = words.Skip(verbAt + 1).ToList();
                if (verb == "cd")
                {
                    insideFolder = arguments.Count > 0 && arguments[0].Text.Contains(Folder);
                    continue;
                }
                if (IsTool(verb, arguments)) continue; // the tool reads it itself

                var interpreter = IsInterpreter(verb);
                var harmless = Harmless.Contains(verb);
                var folderNamed = insideFolder || arguments.Any(w => !w.IsText && w.Text.Contains(Folder));
                var pattern = Searchers.Contains(verb) ? PatternIndex(arguments) : -1;

                var candidates = arguments.Concat(simple.Bodies).ToList();
                for (var i = 0; i < candidates.Count; i++)
                {
                    var word = candidates[i];
                    if (i == pattern) continue; // grep -n "graph.json" …/GRAPH_REPORT.md searches for the name
                    if (word.IsText && !interpreter) continue; // a message, a body, a sentence: not a path
                    if (NamesGraphFile(word.Text, folderNamed))
                    {
                        if (!harmless) return true;
                    }
                    else if (pattern >= 0 && !word.IsText && Segments(word.Text).LastOrDefault() == Folder)
                    {
                        return true; // grep -r Health entitas-graph-out
                    }
                }
            }
            return false;
        }

        /// <summary>The word naming the command: after assignments (A=b) and prefixes like env or sudo.</summary>
        static int VerbIndex(IReadOnlyList<ShellWords.Word> words)
        {
            for (var i = 0; i < words.Count; i++)
            {
                var text = words[i].Text;
                if (Regex.IsMatch(text, "^[A-Za-z_][A-Za-z0-9_]*=")) continue;
                if (Prefixes.Contains(text)) continue;
                return i;
            }
            return -1;
        }

        static string Name(string path)
        {
            var segments = Segments(path);
            return segments.Length == 0 ? path : segments[^1];
        }

        static bool IsTool(string verb, List<ShellWords.Word> arguments) =>
            verb is "entitas-graph" or "entitas-graph.cmd" ||
            verb == "dotnet" && arguments.Any(w => !w.IsText && Name(w.Text) == "entitas-graph.dll");

        static bool IsInterpreter(string verb) =>
            Interpreters.Any(name => verb == name || verb.StartsWith(name, StringComparison.Ordinal) && Regex.IsMatch(verb.Substring(name.Length), @"^[0-9.]+$"));

        /// <summary>grep and rg take the pattern first, unless -e, -f or --regexp gives it.</summary>
        static int PatternIndex(List<ShellWords.Word> arguments)
        {
            if (arguments.Any(w => w.Text is "-e" or "-f" or "--regexp" or "--file" || w.Text.StartsWith("--regexp=", StringComparison.Ordinal)))
                return -1;
            for (var i = 0; i < arguments.Count; i++)
                if (!arguments[i].Text.StartsWith("-", StringComparison.Ordinal)) return i;
            return -1;
        }

        /// <summary>
        /// The word names graph.json or graph.html — by name, or by a wildcard that matches it —
        /// in the folder: the word says so itself, or the command names the folder elsewhere, or
        /// an earlier cd went into it.
        /// </summary>
        static bool NamesGraphFile(string word, bool folderNamed)
        {
            var inFolder = folderNamed || word.Contains(Folder);
            if (!inFolder) return false;
            if (GraphFiles.Any(name => word.Contains(name, StringComparison.OrdinalIgnoreCase))) return true;

            var last = Segments(word).LastOrDefault();
            if (last == null || last.IndexOfAny(new[] { '*', '?', '[' }) < 0) return false;
            var regex = "^" + Regex.Escape(last).Replace(@"\*", ".*").Replace(@"\?", ".").Replace(@"\[", "[") + "$";
            try
            {
                return GraphFiles.Any(name => Regex.IsMatch(name, regex));
            }
            catch (ArgumentException)
            {
                return true; // a bracket expression we cannot read: assume it may match
            }
        }
    }

    /// <summary>
    /// A rough sh lexer, enough to tell a path argument from text: simple commands (split at ;, &amp;,
    /// |, newlines and parentheses), their words with quotes removed, and heredoc bodies. Command
    /// substitutions — $( … ) and backticks, also inside double quotes — are read as commands of
    /// their own, so <c>"$(cat …/graph.json)"</c> is still a read while a PR body written through
    /// <c>"$(cat &lt;&lt;'EOF' … EOF)"</c> stays text.
    /// </summary>
    public static class ShellWords
    {
        public sealed class Word
        {
            public Word(string text, bool isText)
            {
                Text = text;
                IsText = isText;
            }

            public string Text { get; }

            /// <summary>Quoted whitespace in it, or a heredoc body: text rather than a path.</summary>
            public bool IsText { get; }
        }

        public sealed class Command
        {
            public List<Word> Words { get; } = new List<Word>();
            public List<Word> Bodies { get; } = new List<Word>();
        }

        public static List<Command> Commands(string script)
        {
            var commands = new List<Command>();
            var i = 0;
            Lex(script, ref i, null, commands);
            return commands;
        }

        sealed class Heredoc
        {
            public string Delimiter = string.Empty;
            public bool StripTabs;
            public Command Owner = null!;
        }

        /// <summary>
        /// Lexes from <paramref name="i"/> until the end, or the character closing a substitution;
        /// returns where the substitution's text ends (the closing character, or the end of an
        /// unclosed one). <paramref name="i"/> never passes the end of the script.
        /// </summary>
        static int Lex(string s, ref int i, char? closing, List<Command> commands)
        {
            var command = new Command();
            var word = new StringBuilder();
            var inWord = false;
            var isText = false;
            var depth = 0;
            var pending = new List<Heredoc>();
            Heredoc? awaitingDelimiter = null;

            void EndWord()
            {
                if (!inWord) return;
                var text = word.ToString();
                if (awaitingDelimiter != null)
                {
                    awaitingDelimiter.Delimiter = text;
                    pending.Add(awaitingDelimiter);
                    awaitingDelimiter = null;
                }
                else
                {
                    command.Words.Add(new Word(text, isText));
                }
                word.Clear();
                inWord = false;
                isText = false;
            }

            void EndCommand()
            {
                EndWord();
                if (command.Words.Count > 0 || command.Bodies.Count > 0) commands.Add(command);
                command = new Command();
            }

            while (i < s.Length)
            {
                var c = s[i];

                if (closing != null && c == closing && depth == 0)
                {
                    EndCommand();
                    return i++;
                }

                switch (c)
                {
                    case ' ':
                    case '\t':
                        EndWord();
                        i++;
                        continue;
                    case '\n':
                        EndCommand();
                        i++;
                        ReadBodies(s, ref i, pending);
                        continue;
                    case '\\':
                        if (i + 1 < s.Length && s[i + 1] == '\n') { i += 2; continue; }
                        if (i + 1 < s.Length) word.Append(s[i + 1]);
                        inWord = true;
                        i = Math.Min(i + 2, s.Length);
                        continue;
                    case '\'':
                    {
                        var end = s.IndexOf('\'', i + 1);
                        if (end < 0) end = s.Length;
                        var quoted = s.Substring(i + 1, end - i - 1);
                        word.Append(quoted);
                        isText |= quoted.Any(char.IsWhiteSpace);
                        inWord = true;
                        i = Math.Min(end + 1, s.Length);
                        continue;
                    }
                    case '"':
                        i++;
                        DoubleQuoted(s, ref i, word, ref isText, commands);
                        inWord = true;
                        continue;
                    case '`':
                    {
                        i++;
                        var start = i;
                        word.Append(s, start, Lex(s, ref i, '`', commands) - start);
                        inWord = true;
                        continue;
                    }
                    case '$' when i + 1 < s.Length && s[i + 1] == '(':
                    {
                        i += 2;
                        var start = i;
                        word.Append(s, start, Lex(s, ref i, ')', commands) - start);
                        inWord = true;
                        continue;
                    }
                    case '#' when !inWord:
                        while (i < s.Length && s[i] != '\n') i++;
                        continue;
                    case ';':
                    case '|':
                        EndCommand();
                        i++;
                        continue;
                    case '&':
                        if (i + 1 < s.Length && s[i + 1] == '>') { EndWord(); i += 2; continue; } // &> redirect
                        EndCommand();
                        i++;
                        continue;
                    case '(':
                        depth++;
                        EndCommand();
                        i++;
                        continue;
                    case ')':
                        if (depth > 0) depth--;
                        EndCommand();
                        i++;
                        continue;
                    case '<':
                    case '>':
                        EndWord();
                        if (c == '<' && i + 1 < s.Length && s[i + 1] == '<' && !(i + 2 < s.Length && s[i + 2] == '<'))
                        {
                            i += 2;
                            var strip = i < s.Length && s[i] == '-';
                            if (strip) i++;
                            awaitingDelimiter = new Heredoc { StripTabs = strip, Owner = command };
                            while (i < s.Length && (s[i] == ' ' || s[i] == '\t')) i++;
                            continue;
                        }
                        while (i < s.Length && (s[i] == '<' || s[i] == '>' || s[i] == '&' || s[i] == '|')) i++;
                        continue;
                    default:
                        word.Append(c);
                        inWord = true;
                        i++;
                        continue;
                }
            }

            EndCommand();
            return s.Length;
        }

        static void DoubleQuoted(string s, ref int i, StringBuilder word, ref bool isText, List<Command> commands)
        {
            while (i < s.Length && s[i] != '"')
            {
                var c = s[i];
                if (c == '\\' && i + 1 < s.Length && s[i + 1] is '"' or '\\' or '$' or '`')
                {
                    word.Append(s[i + 1]);
                    i += 2;
                    continue;
                }
                if (c == '$' && i + 1 < s.Length && s[i + 1] == '(')
                {
                    i += 2;
                    var start = i;
                    var inner = s.Substring(start, Lex(s, ref i, ')', commands) - start);
                    word.Append(inner);
                    isText |= inner.Any(char.IsWhiteSpace);
                    continue;
                }
                if (c == '`')
                {
                    i++;
                    var start = i;
                    var inner = s.Substring(start, Lex(s, ref i, '`', commands) - start);
                    word.Append(inner);
                    isText |= inner.Any(char.IsWhiteSpace);
                    continue;
                }
                isText |= char.IsWhiteSpace(c);
                word.Append(c);
                i++;
            }
            if (i < s.Length) i++; // the closing quote
        }

        /// <summary>After a newline: the bodies of the heredocs started on the line before, in order.</summary>
        static void ReadBodies(string s, ref int i, List<Heredoc> pending)
        {
            foreach (var heredoc in pending)
            {
                var body = new StringBuilder();
                while (i < s.Length)
                {
                    var end = s.IndexOf('\n', i);
                    var line = end < 0 ? s.Substring(i) : s.Substring(i, end - i);
                    i = end < 0 ? s.Length : end + 1;
                    if ((heredoc.StripTabs ? line.TrimStart('\t') : line) == heredoc.Delimiter) break;
                    body.Append(line).Append('\n');
                }
                heredoc.Owner.Bodies.Add(new Word(body.ToString(), isText: true));
            }
            pending.Clear();
        }
    }
}
