using System.IO;
using System.Linq;
using System.Text.Json;
using Entitas.Graph.Mcp;
using Entitas.Graph.Output;
using FluentAssertions;
using Xunit;

namespace Entitas.Graph.Tests
{
    /// <summary>
    /// entitas-graph mcp: the protocol a client speaks to it, and answers that are the CLI's word
    /// for word — on the fixture's graph.
    /// </summary>
    public sealed class McpServerTests
    {
        string? _diffAsked;

        McpServer Server(int budget = GraphQueries.DefaultBudget) =>
            new McpServer(() => FixtureProject.Graph, (commit, from, to) => _diffAsked = $"{commit}|{from}|{to}", budget, "9.9.9");

        static JsonElement Message(string? line)
        {
            line.Should().NotBeNull();
            return JsonDocument.Parse(line!).RootElement;
        }

        static string Call(int id, string tool, string arguments) =>
            $"{{\"jsonrpc\":\"2.0\",\"id\":{id},\"method\":\"tools/call\",\"params\":{{\"name\":\"{tool}\",\"arguments\":{arguments}}}}}";

        static (string Text, bool IsError) Answer(string? line)
        {
            var result = Message(line).GetProperty("result");
            var isError = result.TryGetProperty("isError", out var flag) && flag.GetBoolean();
            return (result.GetProperty("content")[0].GetProperty("text").GetString()!, isError);
        }

        [Fact]
        public void Initialize_agrees_on_the_clients_version_when_it_knows_it_and_offers_its_own_otherwise()
        {
            var known = Message(Server().Handle("{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"initialize\",\"params\":{\"protocolVersion\":\"2025-06-18\",\"capabilities\":{}}}"));
            known.GetProperty("id").GetInt32().Should().Be(1);
            var result = known.GetProperty("result");
            result.GetProperty("protocolVersion").GetString().Should().Be("2025-06-18");
            result.GetProperty("capabilities").GetProperty("tools").ValueKind.Should().Be(JsonValueKind.Object);
            result.GetProperty("serverInfo").GetProperty("name").GetString().Should().Be("entitas-graph");
            result.GetProperty("serverInfo").GetProperty("version").GetString().Should().Be("9.9.9");

            var unknown = Message(Server().Handle("{\"jsonrpc\":\"2.0\",\"id\":\"a\",\"method\":\"initialize\",\"params\":{\"protocolVersion\":\"2099-01-01\"}}"));
            unknown.GetProperty("id").GetString().Should().Be("a", "the id comes back as it was sent");
            unknown.GetProperty("result").GetProperty("protocolVersion").GetString().Should().Be("2025-11-25");
        }

        [Fact]
        public void Notifications_get_no_answer_and_anything_else_unknown_an_error()
        {
            var server = Server();
            server.Handle("{\"jsonrpc\":\"2.0\",\"method\":\"notifications/initialized\"}").Should().BeNull();
            Message(server.Handle("{\"jsonrpc\":\"2.0\",\"id\":7,\"method\":\"ping\"}")).GetProperty("result").EnumerateObject().Should().BeEmpty();
            Message(server.Handle("{\"jsonrpc\":\"2.0\",\"id\":8,\"method\":\"resources/list\"}")).GetProperty("error").GetProperty("code").GetInt32().Should().Be(-32601);
            Message(server.Handle("not json")).GetProperty("error").GetProperty("code").GetInt32().Should().Be(-32700);
            Message(server.Handle(Call(9, "rm_rf", "{}"))).GetProperty("error").GetProperty("code").GetInt32().Should().Be(-32602);
        }

        [Fact]
        public void Every_query_command_is_a_read_only_tool_with_its_arguments_described()
        {
            var tools = Message(Server().Handle("{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"tools/list\"}")).GetProperty("result").GetProperty("tools").EnumerateArray().ToList();

            tools.Select(t => t.GetProperty("name").GetString()).Should().Equal(
                "who_creates", "who_reads", "lifecycle", "systems_of", "explain", "path", "query", "flow", "report", "diff");
            foreach (var tool in tools)
            {
                tool.GetProperty("description").GetString().Should().NotBeNullOrWhiteSpace();
                tool.GetProperty("annotations").GetProperty("readOnlyHint").GetBoolean().Should().BeTrue();
                tool.GetProperty("inputSchema").GetProperty("type").GetString().Should().Be("object");
            }
            var flow = tools.Single(t => t.GetProperty("name").GetString() == "flow").GetProperty("inputSchema");
            flow.GetProperty("required").EnumerateArray().Select(r => r.GetString()).Should().Equal("name");
            flow.GetProperty("properties").GetProperty("depth").GetProperty("type").GetString().Should().Be("integer");
        }

        [Fact]
        public void A_tool_answers_what_the_command_line_prints()
        {
            var queries = new GraphQueries(FixtureProject.Graph);
            var server = Server();

            Answer(server.Handle(Call(1, "who_reads", "{\"component\":\"Furniture\"}"))).Should().Be((queries.WhoReads("Furniture"), false));
            Answer(server.Handle(Call(2, "flow", "{\"name\":\"PlaceFurnitureRequest\",\"depth\":2}"))).Should().Be((queries.Flow("PlaceFurnitureRequest", 2), false));
            Answer(server.Handle(Call(3, "query", "{\"words\":\"furniture placement\",\"limit\":\"3\"}"))).Should().Be((queries.Query("furniture placement", 3), false));
            Answer(server.Handle(Call(4, "systems_of", "{\"feature\":\"GameplayFeature\"}"))).Should().Be((queries.SystemsOf("GameplayFeature"), false));
            Answer(server.Handle(Call(5, "report", "{}"))).Should().Be((GraphReport.Build(FixtureProject.Graph), false));
            Answer(Server(budget: 60).Handle(Call(6, "flow", "{\"name\":\"RequestPlacementSystem\"}"))).Text
                .Should().Be(new GraphQueries(FixtureProject.Graph, budget: 60).Flow("RequestPlacementSystem"), "the budget is the server's, as on the command line");
        }

        [Fact]
        public void A_name_or_an_argument_it_cannot_use_is_an_error_result_the_assistant_can_read()
        {
            var server = Server();

            var missing = Answer(server.Handle(Call(1, "who_reads", "{\"component\":\"Furnitur\"}")));
            missing.IsError.Should().BeTrue();
            missing.Text.Should().Contain("Did you mean:").And.Contain("Furniture");

            Answer(server.Handle(Call(2, "who_reads", "{}"))).Should().Be(("'component' is required", true));
            Answer(server.Handle(Call(3, "flow", "{\"name\":\"Furniture\",\"depth\":99}"))).Should().Be(("'depth' is 1 to 8", true));
            Answer(server.Handle(Call(4, "query", "{\"words\":\"furniture\",\"limit\":\"many\"}"))).Should().Be(("'limit' must be a whole number", true));
        }

        [Fact]
        public void Diff_takes_a_commit_or_refs_and_says_which_when_given_neither()
        {
            var server = Server();

            Answer(server.Handle(Call(1, "diff", "{\"ref\":\"master\"}"))).Should().Be(("|master|", false));
            Answer(server.Handle(Call(2, "diff", "{\"commit\":\"abc123\"}"))).Should().Be(("abc123||", false));
            Answer(server.Handle(Call(3, "diff", "{\"ref\":\"v1\",\"ref2\":\"v2\"}"))).Should().Be(("|v1|v2", false));

            _diffAsked = null;
            Answer(server.Handle(Call(4, "diff", "{}"))).Should().Be(("diff needs commit or ref", true));
            Answer(server.Handle(Call(5, "diff", "{\"commit\":\"a\",\"ref\":\"b\"}"))).IsError.Should().BeTrue();
            Answer(server.Handle(Call(6, "diff", "{\"ref2\":\"b\"}"))).IsError.Should().BeTrue();
            _diffAsked.Should().BeNull("nothing ran for a call it refused");
        }

        [Fact]
        public void Run_answers_line_by_line_and_stops_at_the_end_of_its_input()
        {
            var input = new StringReader(
                "{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"initialize\",\"params\":{\"protocolVersion\":\"2024-11-05\"}}\n" +
                "{\"jsonrpc\":\"2.0\",\"method\":\"notifications/initialized\"}\n\n" +
                Call(2, "who_creates", "{\"component\":\"Furniture\"}") + "\n");
            var output = new StringWriter();

            Server().Run(input, output);

            var lines = output.ToString().Split('\n', System.StringSplitOptions.RemoveEmptyEntries);
            lines.Should().HaveCount(2, "one answer per request, none for the notification");
            lines.Select(l => Message(l).GetProperty("id").GetInt32()).Should().Equal(1, 2);
            Answer(lines[1]).Text.Should().Be(new GraphQueries(FixtureProject.Graph).WhoCreates("Furniture"));
        }
    }
}
