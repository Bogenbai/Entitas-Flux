using System;
using System.Linq;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Entitas.Graph.Setup
{
    /// <summary>
    /// The Claude Code hook <c>entitas-graph install</c> adds to <c>.claude/settings.json</c>: a
    /// PreToolUse hook on Read, Grep, Glob and Bash that runs <c>./entitas-graph --claude-hook</c>,
    /// which stops a call reading graph.json or graph.html and tells the assistant what to ask the
    /// graph instead. The block in CLAUDE.md asks the same; this is what holds when an assistant
    /// forgets. Strings in, strings out: the rest of the settings file is kept as it was, and its
    /// own hooks with it.
    /// </summary>
    public static class ClaudeHook
    {
        public const string Flag = "--claude-hook";
        public const string Matcher = "Read|Grep|Glob|Bash";
        public const string File = ".claude/settings.json";

        /// <summary>What the assistant reads when a call is stopped (<see cref="ClaudeHookCheck"/> decides which).</summary>
        public const string Message =
            "entitas-graph: graph.json and graph.html are for programs and browsers (graph.json is about 1.4 M tokens on a 2000-file project). " +
            "Ask the graph instead: ./entitas-graph who-reads <Component>, who-creates, flow <Request|System>, explain <Name>, query 'words' " +
            "- ./entitas-graph --help lists every command. GRAPH_REPORT.md next to them is fine to read.";

        /// <summary>The hook's command: the sh wrapper in the project root, where Claude Code's CLAUDE_PROJECT_DIR points.</summary>
        public static string Command => "\"$CLAUDE_PROJECT_DIR\"/" + Wrappers.ShName + " " + Flag;

        static readonly JsonSerializerOptions Options = new JsonSerializerOptions
        {
            WriteIndented = true,
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
        };

        /// <summary>
        /// The settings with the hook in them, replacing an older one of ours; the input itself
        /// when nothing changes. Written as Claude Code writes it — two-space indent — with the
        /// file's own line breaks and final newline.
        /// </summary>
        /// <exception cref="FormatException">The file is not a JSON object.</exception>
        public static string Install(string? existing)
        {
            var root = Parse(existing) ?? new JsonObject();
            var hooks = root["hooks"] as JsonObject;
            if (hooks == null) root["hooks"] = hooks = new JsonObject();
            var pre = hooks["PreToolUse"] as JsonArray;
            if (pre == null) hooks["PreToolUse"] = pre = new JsonArray();

            var ours = pre.Where(IsOurs).ToList();
            if (ours.Count == 1 && Same(ours[0], Entry()))
                return existing!;
            foreach (var entry in ours) pre.Remove(entry);
            pre.Add(Entry());
            return Write(root, existing);
        }

        /// <summary>The settings without our hook, an empty string when nothing else was in them, or null when the hook was not there.</summary>
        /// <exception cref="FormatException">The file is not a JSON object.</exception>
        public static string? Uninstall(string existing)
        {
            var root = Parse(existing);
            if (root?["hooks"] is not JsonObject hooks || hooks["PreToolUse"] is not JsonArray pre) return null;
            var ours = pre.Where(IsOurs).ToList();
            if (ours.Count == 0) return null;

            foreach (var entry in ours) pre.Remove(entry);
            if (pre.Count == 0) hooks.Remove("PreToolUse");
            if (hooks.Count == 0) root.Remove("hooks");
            return root.Count == 0 ? string.Empty : Write(root, existing);
        }

        /// <summary>Whether the settings carry our hook exactly as this version writes it.</summary>
        public static bool IsCurrent(string existing)
        {
            try
            {
                return Parse(existing)?["hooks"]?["PreToolUse"] is JsonArray pre &&
                       pre.Where(IsOurs).Count() == 1 && Same(pre.First(IsOurs), Entry());
            }
            catch (FormatException)
            {
                return false;
            }
        }

        /// <summary>Whether the settings carry a hook of ours, of any version.</summary>
        public static bool Has(string existing)
        {
            try
            {
                return Parse(existing)?["hooks"]?["PreToolUse"] is JsonArray pre && pre.Any(IsOurs);
            }
            catch (FormatException)
            {
                return false;
            }
        }

        /// <summary>Equal JSON; netstandard/.NET 6 have no JsonNode.DeepEquals.</summary>
        internal static bool Same(JsonNode? a, JsonNode b) => a != null && a.ToJsonString() == b.ToJsonString();

        static JsonNode Entry() => new JsonObject
        {
            ["matcher"] = Matcher,
            ["hooks"] = new JsonArray(new JsonObject { ["type"] = "command", ["command"] = Command })
        };

        /// <summary>An entry of ours: one whose command runs the wrapper with --claude-hook.</summary>
        static bool IsOurs(JsonNode? entry) =>
            entry?["hooks"] is JsonArray hooks &&
            hooks.Any(h => h?["command"] is JsonValue value && value.TryGetValue<string>(out var command) &&
                           command.Contains(Wrappers.ShName) && command.Contains(Flag));

        internal static JsonObject? Parse(string? text, string file = File)
        {
            if (string.IsNullOrWhiteSpace(text)) return null;
            try
            {
                return JsonNode.Parse(text, documentOptions: new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true }) as JsonObject
                       ?? throw new FormatException($"{file} is not a JSON object");
            }
            catch (JsonException ex)
            {
                throw new FormatException($"{file} is not valid JSON ({ex.Message})");
            }
        }

        internal static string Write(JsonObject root, string? existing)
        {
            var newline = existing != null && existing.Contains("\r\n") ? "\r\n" : "\n";
            var text = root.ToJsonString(Options).Replace("\r\n", "\n").Replace("\n", newline);
            return existing == null || existing.EndsWith("\n", StringComparison.Ordinal) ? text + newline : text;
        }
    }
}
