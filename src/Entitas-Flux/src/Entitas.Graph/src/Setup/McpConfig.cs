using System;
using System.Linq;
using System.Text.Json.Nodes;

namespace Entitas.Graph.Setup
{
    /// <summary>
    /// The MCP server <c>entitas-graph install</c> registers in the project's <c>.mcp.json</c>, next
    /// to the wrappers: the query commands as tools for subagents that have no shell (a Claude Code
    /// agent whose <c>tools:</c> is Read, Grep, Glob can add <c>mcp__entitas-graph__*</c>). Claude Code
    /// finds the file from any folder below it but starts the server in the folder it was started
    /// in, and neither a relative command nor <c>${CLAUDE_PROJECT_DIR}</c> reaches the wrapper from
    /// a subfolder — so sh asks git where the repository is. Strings in, strings out, like
    /// <see cref="ClaudeHook"/>: other servers in the file are kept as they were.
    /// </summary>
    public static class McpConfig
    {
        public const string File = ".mcp.json";
        public const string Server = Mcp.McpServer.Name;

        /// <summary>The script sh runs: the wrapper in the repository root (or the current folder outside git), with mcp.</summary>
        public static string Script =>
            "root=$(git rev-parse --show-toplevel 2>/dev/null) || root=.; exec \"$root/" + Wrappers.ShName + "\" mcp";

        /// <summary>The file with our server in it; the input itself when nothing changes.</summary>
        /// <exception cref="FormatException">Not a JSON object, or an entitas-graph server install did not write.</exception>
        public static string Install(string? existing)
        {
            var root = ClaudeHook.Parse(existing, File) ?? new JsonObject();
            var servers = root["mcpServers"] as JsonObject;
            if (servers == null) root["mcpServers"] = servers = new JsonObject();

            var current = servers[Server];
            if (current != null && !IsOurs(current))
                throw new FormatException($"its '{Server}' server was not written by entitas-graph install — rename it, then run install again");
            if (current != null && ClaudeHook.Same(current, Entry()))
                return existing!;

            servers[Server] = Entry();
            return ClaudeHook.Write(root, existing);
        }

        /// <summary>The file without our server, an empty string when nothing else was in it, or null when it was not there.</summary>
        public static string? Uninstall(string existing)
        {
            var root = ClaudeHook.Parse(existing, File);
            if (root?["mcpServers"] is not JsonObject servers || !IsOurs(servers[Server])) return null;

            servers.Remove(Server);
            if (servers.Count == 0) root.Remove("mcpServers");
            return root.Count == 0 ? string.Empty : ClaudeHook.Write(root, existing);
        }

        /// <summary>Whether the file has a server of ours that differs from what this version writes.</summary>
        public static bool IsOutdated(string existing)
        {
            try
            {
                var current = ClaudeHook.Parse(existing, File)?["mcpServers"]?[Server];
                return IsOurs(current) && !ClaudeHook.Same(current, Entry());
            }
            catch (FormatException)
            {
                return false;
            }
        }

        static JsonObject Entry() => new JsonObject
        {
            ["type"] = "stdio",
            ["command"] = "sh",
            ["args"] = new JsonArray("-c", Script),
        };

        /// <summary>A server of ours: one that runs the wrapper with mcp.</summary>
        static bool IsOurs(JsonNode? server) =>
            server?["args"] is JsonArray args &&
            args.Any(a => a is JsonValue value && value.TryGetValue<string>(out var text) && text.Contains("/" + Wrappers.ShName + "\" mcp"));
    }
}
