using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Text.Json;

namespace Entitas.Graph.Output
{
    /// <summary>
    /// graph.html: one self-contained page — vis-network 9.1.9 (dual Apache-2.0/MIT, licence
    /// banner kept) and the graph data inlined, so it opens offline, straight from disk.
    /// </summary>
    public static class GraphHtml
    {
        /// <summary>
        /// Relations the page draws against their stored direction. Every edge is stored as what
        /// a system does to a component, so a reader's arrow pointed into the component like a
        /// writer's and the picture showed every system writing. Drawn as data flows, the arrow of
        /// a read, a NoneOf or a [Watched] reaction runs from the component into the system;
        /// creates, removes and destroys keep system → component, contains feature → system.
        /// </summary>
        public static readonly string[] DrawnComponentToSystem = { Relation.Reads, Relation.Filters, Relation.Reacts };

        public static string Build(EcsGraph graph)
        {
            var template = Resource("graph.html");
            var vis = Resource("vis-network.min.js");

            using var stream = new MemoryStream();
            using (var w = new Utf8JsonWriter(stream))
            {
                w.WriteStartObject();
                w.WriteString("project", graph.Metadata.Project);
                if (graph.Metadata.BuiltAtCommit != null) w.WriteString("commit", graph.Metadata.BuiltAtCommit);
                if (!string.IsNullOrEmpty(graph.Metadata.ToolVersion)) w.WriteString("tool_version", graph.Metadata.ToolVersion);
                w.WriteStartArray("reversed");
                foreach (var relation in DrawnComponentToSystem)
                    w.WriteStringValue(relation);
                w.WriteEndArray();
                w.WriteStartArray("nodes");
                foreach (var n in graph.Nodes)
                {
                    w.WriteStartObject();
                    w.WriteString("id", n.Id);
                    w.WriteString("label", n.Label);
                    w.WriteString("kind", n.Kind);
                    // The page colours and offers `flow` by role, so a request found by structure counts as one.
                    if (n.MessageRole != null) w.WriteString("role", n.MessageRole);
                    if (n.Generated) w.WriteBoolean("generated", true);
                    if (n.SourceFile != null) w.WriteString("file", n.SourceFile);
                    if (n.Line > 0) w.WriteNumber("line", n.Line);
                    var info = Info(graph, n);
                    if (info.Length > 0) w.WriteString("info", info);
                    w.WriteEndObject();
                }
                w.WriteEndArray();
                w.WriteStartArray("edges");
                foreach (var e in graph.Edges)
                {
                    w.WriteStartObject();
                    w.WriteString("from", e.Source);
                    w.WriteString("to", e.Target);
                    w.WriteString("relation", e.Relation);
                    w.WriteString("via", e.Via);
                    w.WriteString("confidence", e.Confidence);
                    if (e.Through != null) w.WriteString("through", e.Through);
                    if (e.Condition != null) w.WriteString("condition", e.Condition);
                    if (e.SourceFile != null) w.WriteString("file", e.SourceFile);
                    if (e.Line > 0) w.WriteNumber("line", e.Line);
                    w.WriteEndObject();
                }
                w.WriteEndArray();
                w.WriteEndObject();
            }

            // Inside a <script>, "</" would end it early.
            var data = Encoding.UTF8.GetString(stream.ToArray()).Replace("</", "<\\/");
            return template
                .Replace("/*__VIS__*/", vis)
                .Replace("/*__DATA__*/", "const GRAPH = " + data + ";");
        }

        static string Info(EcsGraph graph, GraphNode n)
        {
            if (n.Kind == NodeKind.Component)
                return string.Join(", ", new[] { string.Join("/", n.Contexts), n.Flag ? "flag" : "data" }.Concat(n.Attributes));

            return string.Join("; ", n.Placements.Select(p =>
                $"{graph.Node(p.Root)?.Label} #{p.Order}" + (p.Chain.Count > 1 ? $" (via {graph.Node(p.Chain[p.Chain.Count - 1])?.Label})" : string.Empty)));
        }

        static string Resource(string name)
        {
            using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(name)
                               ?? throw new FileNotFoundException($"embedded resource {name} is missing");
            using var reader = new StreamReader(stream, Encoding.UTF8);
            return reader.ReadToEnd();
        }
    }
}
