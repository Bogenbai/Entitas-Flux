using System;
using System.Linq;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Entitas.Graph.Setup
{
    /// <summary>
    /// The MCP server <c>entitas-graph install</c> registers in the project's <c>.mcp.json</c>, next
    /// to the wrappers: the query commands as tools for subagents that have no shell (a Claude Code
    /// agent whose <c>tools:</c> is Read, Grep, Glob can add <c>mcp__entitas-graph__*</c>). One entry
    /// serves every teammate, so its command must start on macOS, Linux and Windows alike: git. A
    /// git alias that starts with ! runs in git's own sh (on Windows the one Git for Windows ships,
    /// whether or not it is on PATH) from the top of the repository — and Claude Code starts the
    /// server in whatever folder it was started in, while finding .mcp.json from any folder below
    /// it. <c>sh</c> as the command did not start on Windows; <c>cmd /c entitas-graph.cmd</c> only
    /// there, and that one connected but never delivered its tool list. Strings in, strings out,
    /// like <see cref="ClaudeHook"/>: other servers in the file are kept as they were.
    /// </summary>
    public static class McpConfig
    {
        public const string File = ".mcp.json";
        public const string Server = Mcp.McpServer.Name;

        /// <summary>The alias git runs: the sh wrapper with mcp, from the top of the repository (the current folder outside one).</summary>
        public const string Alias = "!sh ./" + Wrappers.ShName + " mcp";

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
            ["command"] = "git",
            ["args"] = new JsonArray("-c", "alias." + Server + "=" + Alias, Server),
        };

        /// <summary>A server of ours: one that runs the wrapper with mcp — this version's git alias, or the sh -c of 0.6.2.</summary>
        static bool IsOurs(JsonNode? server) =>
            server?["args"] is JsonArray args &&
            args.Any(a => a is JsonValue value && value.TryGetValue<string>(out var text) &&
                          Regex.IsMatch(text, Regex.Escape(Wrappers.ShName) + "\"? mcp\\b"));
    }
}
