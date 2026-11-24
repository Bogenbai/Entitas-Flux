using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using Entitas.Graph.Output;

namespace Entitas.Graph.Mcp
{
    /// <summary>
    /// <c>entitas-graph mcp</c>: the query commands as tools of a Model Context Protocol server on
    /// stdio, for assistants and subagents that have no shell — a Claude Code agent whose
    /// <c>tools:</c> lists Read, Grep and Glob can list these too, without being given Bash. One
    /// JSON-RPC message per line in and out; anything else the tool prints goes to stderr. The
    /// answers are the CLI's, word for word, under the same token budget. The graph is kept in
    /// memory between calls and rebuilt when the sources change (<see cref="Graph"/> decides).
    /// </summary>
    public sealed class McpServer
    {
        public const string Name = "entitas-graph";

        /// <summary>Protocol versions this server speaks: it uses tools only, which none of them changed.</summary>
        static readonly string[] Versions = { "2025-11-25", "2025-06-18", "2025-03-26", "2024-11-05" };

        /// <summary>
        /// Every reply is plain ASCII: anything else is escaped (\u2192), so no code page between
        /// the tool and the client can change a byte of it. On Windows the replies whose text has
        /// a dash or an arrow in it — the tool list — were the ones that never arrived.
        /// </summary>
        static readonly JsonSerializerOptions Json = new JsonSerializerOptions { Encoder = JavaScriptEncoder.Default };

        readonly Func<EcsGraph> _graph;
        readonly Func<string?, string?, string?, string>? _diff;
        readonly int _budget;
        readonly string _version;

        /// <param name="graph">The current graph: built, loaded or kept, as the sources dictate.</param>
        /// <param name="diff">diff by (commit, ref, second ref); null leaves the tool out.</param>
        public McpServer(Func<EcsGraph> graph, Func<string?, string?, string?, string>? diff, int budget, string version)
        {
            _graph = graph;
            _diff = diff;
            _budget = budget;
            _version = version;
        }

        /// <param name="log">Where each request is noted with its time (stderr, which the client keeps in its log); null for none.</param>
        public void Run(TextReader input, TextWriter output, TextWriter? log = null)
        {
            string? line;
            while ((line = input.ReadLine()) != null)
            {
                if (line.Trim().Length == 0) continue;
                var started = System.Diagnostics.Stopwatch.StartNew();
                var response = Handle(line);
                if (response == null) continue;
                output.Write(response + "\n");
                output.Flush();
                log?.WriteLine($"{Name}: {Method(line)} answered in {started.ElapsedMilliseconds} ms ({response.Length} bytes)");
                log?.Flush();
            }
        }

        static string Method(string line)
        {
            try
            {
                using var document = JsonDocument.Parse(line);
                return document.RootElement.TryGetProperty("method", out var method) ? method.ToString() : "?";
            }
            catch (JsonException)
            {
                return "?";
            }
        }

        /// <summary>The answer to one message, or null for a notification.</summary>
        public string? Handle(string message)
        {
            JsonNode? request;
            try
            {
                request = JsonNode.Parse(message);
            }
            catch (JsonException)
            {
                return Error(null, -32700, "parse error");
            }
            if (request is not JsonObject call) return Error(null, -32600, "invalid request");
            if (Kind(call["method"]) != JsonValueKind.String) return Error(Clone(call["id"]), -32600, "invalid request");

            var id = Clone(call["id"]);
            var method = call["method"]!.GetValue<string>();
            var parameters = call["params"] as JsonObject;
            if (id == null) return null; // a notification (notifications/initialized, cancelled…): nothing to answer

            try
            {
                return method switch
                {
                    "initialize" => Result(id, Initialize(parameters)),
                    "ping" => Result(id, new JsonObject()),
                    "tools/list" => Result(id, new JsonObject { ["tools"] = new JsonArray(Tools().Select(t => (JsonNode)t.Describe()).ToArray()) }),
                    "tools/call" => Result(id, CallTool(parameters)),
                    _ => Error(id, -32601, $"method not found: {method}"),
                };
            }
            catch (InvalidParamsException ex)
            {
                return Error(id, -32602, ex.Message);
            }
        }

        JsonObject Initialize(JsonObject? parameters)
        {
            var asked = Kind(parameters?["protocolVersion"]) == JsonValueKind.String ? parameters["protocolVersion"]!.GetValue<string>() : null;
            return new JsonObject
            {
                ["protocolVersion"] = asked != null && Versions.Contains(asked) ? asked : Versions[0],
                ["capabilities"] = new JsonObject { ["tools"] = new JsonObject { ["listChanged"] = false } },
                ["serverInfo"] = new JsonObject { ["name"] = Name, ["version"] = _version },
                ["instructions"] =
                    "The ECS data flow of this Entitas project, from the compiler's view of the code: who creates, reads, removes or " +
                    "destroys a component, what a feature runs in which order, what happens after a request or event. Ask it before " +
                    "searching the code for those; then open the files an answer names (links marked * and frames are inferred).",
            };
        }

        JsonObject CallTool(JsonObject? parameters)
        {
            var name = Kind(parameters?["name"]) == JsonValueKind.String ? parameters["name"]!.GetValue<string>() : null;
            var tool = Tools().FirstOrDefault(t => t.Name == name) ?? throw new InvalidParamsException($"unknown tool: {name}");
            var arguments = parameters?["arguments"] as JsonObject ?? new JsonObject();

            string text;
            var isError = false;
            try
            {
                text = tool.Answer(new Arguments(arguments));
            }
            catch (InvalidParamsException ex)
            {
                (text, isError) = (ex.Message, true);
            }
            catch (AmbiguousNameException ex)
            {
                (text, isError) = (NameErrors.Ambiguous(ex), true);
            }
            catch (NodeNotFoundException ex)
            {
                (text, isError) = (NameErrors.NotFound(ex), true);
            }
            catch (Exception ex)
            {
                (text, isError) = ("error: " + ex.Message, true);
            }

            var result = new JsonObject { ["content"] = new JsonArray(new JsonObject { ["type"] = "text", ["text"] = text }) };
            if (isError) result["isError"] = true;
            return result;
        }

        GraphQueries Queries() => new GraphQueries(_graph(), _budget);

        IEnumerable<Tool> Tools()
        {
            const string component = "A component's name, as in code (Health or HealthComponent); a partial name lists candidates.";
            yield return QueryTool("who_creates",
                "Systems and other types that create a component: AddX/ReplaceX, isX = true, factories and helpers credited to the systems calling them, with conditions.",
                q => q.WhoCreates, ("component", component, true));
            yield return QueryTool("who_reads",
                "Systems that read a component: the groups that collect it (AllOf/AnyOf/NoneOf), accessors, [Watched] reactions — each with its feature and position.",
                q => q.WhoReads, ("component", component, true));
            yield return QueryTool("lifecycle",
                "A component from creation to removal: who creates, reads, removes and destroys it, per root feature in execution order.",
                q => q.Lifecycle, ("component", component, true));
            yield return QueryTool("systems_of",
                "The systems a feature runs, in order, nested features expanded.",
                q => q.SystemsOf, ("feature", "A feature's name.", true));
            yield return QueryTool("explain",
                "One node — a component, system, feature or type — with all its connections.",
                q => q.Explain, ("name", "Its name.", true));
            yield return new Tool("path",
                "The shortest data-flow chain from one node to another (component → system → component …).",
                args => Queries().Path(args.Text("from"), args.Text("to")),
                ("from", "Where the chain starts.", true), ("to", "Where it ends.", true));
            yield return new Tool("query",
                "The connected piece of the graph around the nodes some words match best — for a topic rather than a name (\"furniture placement\").",
                args => Queries().Query(args.Text("words"),
                    args.Number("limit", GraphQueries.DefaultQuerySeeds, 1, 20),
                    args.Number("depth", GraphQueries.DefaultQueryDepth, 1, GraphQueries.MaxQueryDepth)),
                ("words", "Words naming the topic.", true),
                ("limit", $"Seed nodes, 1-20 (default {GraphQueries.DefaultQuerySeeds}).", false),
                ("depth", $"Steps from the seeds, 1-{GraphQueries.MaxQueryDepth} (default {GraphQueries.DefaultQueryDepth}).", false));
            yield return new Tool("flow",
                "What happens after a request, event, [Watched] change or system: the systems it reaches, in order, each in which frame, and where it waits.",
                args => Queries().Flow(args.Text("name"), args.Number("depth", GraphQueries.DefaultFlowDepth, 1, GraphQueries.MaxFlowDepth)),
                ("name", "A request, event, component or system.", true),
                ("depth", $"System hops to follow, 1-{GraphQueries.MaxFlowDepth} (default {GraphQueries.DefaultFlowDepth}).", false));
            yield return new Tool("report",
                "Findings over the whole project: requests nobody handles, flags never cleared, components read but never created, and more. Long: read it once.",
                _ => GraphReport.Build(_graph()));
            if (_diff != null)
                yield return new Tool("diff",
                    "What changed in the data flow: one commit (commit), the working tree since a branch or commit (ref — for reviewing a branch), or between two (ref and ref2).",
                    args =>
                    {
                        var (commit, from, to) = (args.Optional("commit"), args.Optional("ref"), args.Optional("ref2"));
                        if (commit == null && from == null) throw new InvalidParamsException("diff needs commit or ref");
                        if (commit != null && from != null) throw new InvalidParamsException("diff takes commit or ref, not both");
                        if (to != null && from == null) throw new InvalidParamsException("ref2 needs ref");
                        return _diff(commit, from, to);
                    },
                    ("commit", "A commit: compared with its first parent.", false),
                    ("ref", "A branch or commit: the working tree (or ref2) compared with it.", false),
                    ("ref2", "A second branch or commit, the newer side.", false));
        }

        Tool QueryTool(string name, string description, Func<GraphQueries, Func<string, string>> pick, (string Name, string Description, bool Required) parameter) =>
            new Tool(name, description, args => pick(Queries())(args.Text(parameter.Name)), parameter);

        sealed class Tool
        {
            public readonly string Name;
            readonly string _description;
            readonly (string Name, string Description, bool Required)[] _parameters;
            readonly Func<Arguments, string> _answer;

            public Tool(string name, string description, Func<Arguments, string> answer, params (string Name, string Description, bool Required)[] parameters)
            {
                Name = name;
                _description = description;
                _answer = answer;
                _parameters = parameters;
            }

            public string Answer(Arguments arguments) => _answer(arguments);

            public JsonObject Describe()
            {
                var properties = new JsonObject();
                foreach (var p in _parameters)
                    properties[p.Name] = new JsonObject
                    {
                        ["type"] = p.Description.Contains(" 1-", StringComparison.Ordinal) ? "integer" : "string",
                        ["description"] = p.Description,
                    };
                return new JsonObject
                {
                    ["name"] = Name,
                    ["description"] = _description,
                    ["inputSchema"] = new JsonObject
                    {
                        ["type"] = "object",
                        ["properties"] = properties,
                        ["required"] = new JsonArray(_parameters.Where(p => p.Required).Select(p => (JsonNode)p.Name).ToArray()),
                    },
                    ["annotations"] = new JsonObject { ["readOnlyHint"] = true, ["openWorldHint"] = false },
                };
            }
        }

        sealed class Arguments
        {
            readonly JsonObject _values;

            public Arguments(JsonObject values) => _values = values;

            public string? Optional(string name)
            {
                var value = _values[name];
                if (value == null) return null;
                if (Kind(value) != JsonValueKind.String) throw new InvalidParamsException($"'{name}' must be a string");
                var text = value.GetValue<string>().Trim();
                return text.Length == 0 ? null : text;
            }

            public string Text(string name) => Optional(name) ?? throw new InvalidParamsException($"'{name}' is required");

            public int Number(string name, int fallback, int min, int max)
            {
                var value = _values[name];
                if (value == null) return fallback;
                var number = Kind(value) switch
                {
                    JsonValueKind.Number when value.AsValue().TryGetValue<int>(out var n) => n,
                    JsonValueKind.String when int.TryParse(value.GetValue<string>(), out var n) => n,
                    _ => throw new InvalidParamsException($"'{name}' must be a whole number"),
                };
                return number >= min && number <= max ? number : throw new InvalidParamsException($"'{name}' is {min} to {max}");
            }
        }

        sealed class InvalidParamsException : Exception
        {
            public InvalidParamsException(string message) : base(message) { }
        }

        /// <summary>JsonNode.GetValueKind and DeepClone came with .NET 8; the tool runs on 6.</summary>
        static JsonValueKind Kind(JsonNode? node) => node switch
        {
            null => JsonValueKind.Undefined,
            JsonObject => JsonValueKind.Object,
            JsonArray => JsonValueKind.Array,
            JsonValue value when value.TryGetValue<JsonElement>(out var element) => element.ValueKind,
            JsonValue value when value.TryGetValue<string>(out _) => JsonValueKind.String,
            _ => JsonValueKind.Number,
        };

        static JsonNode? Clone(JsonNode? node) => node == null ? null : JsonNode.Parse(node.ToJsonString());

        static string Result(JsonNode id, JsonNode result) =>
            new JsonObject { ["jsonrpc"] = "2.0", ["id"] = id, ["result"] = result }.ToJsonString(Json);

        static string Error(JsonNode? id, int code, string message) =>
            new JsonObject { ["jsonrpc"] = "2.0", ["id"] = id, ["error"] = new JsonObject { ["code"] = code, ["message"] = message } }.ToJsonString(Json);
    }
}
