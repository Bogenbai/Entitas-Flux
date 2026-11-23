using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;

namespace Entitas.Graph
{
    public static class NodeKind
    {
        public const string Component = "component";
        public const string System = "system";
        public const string Feature = "feature";

        /// <summary>Any other type that touches components: a query, factory, service, behaviour-tree node…</summary>
        public const string Type = "type";
    }

    public static class Relation
    {
        /// <summary>AddX, ReplaceX, isX = true, a context's SetX, an atomic setter.</summary>
        public const string Creates = "creates";

        /// <summary>A group's AllOf/AnyOf, a collector trigger, hasX / isX / entity.X, an entity index lookup.</summary>
        public const string Reads = "reads";

        /// <summary>A group's NoneOf.</summary>
        public const string Filters = "filters";

        /// <summary>RemoveX, SafeRemoveX, isX = false.</summary>
        public const string Removes = "removes";

        /// <summary>Destroy() on entities of a group.</summary>
        public const string Destroys = "destroys";

        /// <summary>Reads the XChanged marker of a [Watched] component.</summary>
        public const string Reacts = "reacts";

        /// <summary>Feature registers a system or feature (Add(...)), with its position.</summary>
        public const string Contains = "contains";
    }

    public static class Confidence
    {
        /// <summary>Written in the type itself.</summary>
        public const string Extracted = "EXTRACTED";

        /// <summary>
        /// Through something else: a query or factory the system calls, or the components an
        /// entity is known to carry (a destroyer of Event+Ready entities destroys every event).
        /// <see cref="GraphEdge.Through"/> says what.
        /// </summary>
        public const string Inferred = "INFERRED";
    }

    public sealed class GraphNode
    {
        public string Id = string.Empty;
        public string Label = string.Empty;
        public string Kind = NodeKind.Type;
        public string? FullName;
        public string? SourceFile;
        public int Line;
        public bool Generated;

        // components
        public List<string> Contexts = new List<string>();
        public bool Flag;
        public List<string> Fields = new List<string>();
        public List<string> Attributes = new List<string>();

        /// <summary>"request" or "event", by the project's naming convention.</summary>
        public string? Role;

        /// <summary>
        /// The systems taking this component off every entity that holds it, in a loop over a
        /// group of it alone (UnmarkJustCreatedEntitiesSystem and JustCreated): no entity keeps
        /// it, so a flag set only on entities carrying it is set once. Empty for most components;
        /// [Cleanup] components get their generated cleanup system here.
        /// </summary>
        public List<string> TransientRemovedBy = new List<string>();

        // systems and features
        public List<string> Interfaces = new List<string>();
        public List<Registration> RegisteredIn = new List<Registration>();
        public List<Placement> Placements = new List<Placement>();
        public bool Root;
    }

    /// <summary>A system or feature as registered by one feature: Add(...) number <see cref="Index"/> (1-based).</summary>
    public sealed class Registration
    {
        public string Feature = string.Empty;
        public int Index;
    }

    /// <summary>Where a system runs under one root feature: its execution position and the features in between.</summary>
    public sealed class Placement
    {
        public string Root = string.Empty;
        public int Order;
        public List<string> Chain = new List<string>();
    }

    public sealed class GraphEdge
    {
        public string Source = string.Empty;
        public string Target = string.Empty;
        public string Relation = string.Empty;

        /// <summary>How: Add, Replace, Flag, AllOf, NoneOf, Access, Destroy, Trigger, EntityIndex…</summary>
        public string Via = string.Empty;

        public string Confidence = Entitas.Graph.Confidence.Extracted;
        public string? Context;

        /// <summary>For INFERRED edges: the call or entity shape it was inferred through.</summary>
        public string? Through;

        /// <summary>
        /// For edges inferred through calls: the methods the system calls in its own code that
        /// lead to <see cref="Through"/> ("FurnitureFactory.CreateFurniture" for an access in
        /// MovementExtensions.UnSetupAllMovements, which the factory calls), "; "-separated.
        /// Absent when that is <see cref="Through"/> itself.
        /// </summary>
        public string? Called;

        public string? SourceFile;
        public int Line;
        public int Count = 1;

        /// <summary>
        /// For writes: "new" when the entity is being created right there (a fluent chain or
        /// a local from a factory), "group" when it comes from a foreach over a group — a state
        /// change on an existing entity.
        /// </summary>
        public string? Entity;

        /// <summary>For contains: the position of the Add(...) in the feature, 1-based.</summary>
        public int Order;

        /// <summary>
        /// For a flag's EXTRACTED creates on existing entities: the transient component (one with
        /// [Cleanup], or with <see cref="GraphNode.TransientRemovedBy"/>) that every group the
        /// setter's loops run over requires. The setter only sees an entity while it carries
        /// that component, so it sets the flag once per entity, on purpose.
        /// </summary>
        public string? OnlyWhile;

        /// <summary>
        /// For creates, removes and destroys made only under a condition: its code, shortened
        /// ("amount &lt; 0", "!(amount &lt; 0)" for an else, "case Kind.X", " &amp;&amp; …" when
        /// more conditions enclose it). Absent when any site of the edge is unconditional; the
        /// distinct conditions of several sites are joined with " or ", the first three named.
        /// For an edge inferred through a call, the call's condition in the system comes first.
        /// </summary>
        public string? Condition;

        public string Key => $"{Source}\u0001{Target}\u0001{Relation}\u0001{Via}\u0001{Confidence}\u0001{Through}\u0001{Entity}";
    }

    public sealed class GraphMetadata
    {
        public int SchemaVersion = 1;
        public string Tool = "entitas-graph";
        public string ToolVersion = string.Empty;
        public string Project = string.Empty;
        public string? BuiltAtCommit;
        public long BuildMilliseconds;
        public int SourceFiles;
        public string RequestSuffix = "Request";
        public string EventSuffix = "Event";

        /// <summary>Destroy() calls whose entity could not be traced to a group.</summary>
        public List<UnresolvedDestroy> UnresolvedDestroys = new List<UnresolvedDestroy>();
    }

    public sealed class UnresolvedDestroy
    {
        public string Source = string.Empty;
        public string File = string.Empty;
        public int Line;
    }

    /// <summary>
    /// The ECS graph. Serialized as node-link JSON, close to Graphify's graph.json (nodes with
    /// id/label/source_file/source_location, links with source/target/relation/confidence) so
    /// tools that read one can mostly read the other — but honestly directed, with ECS fields,
    /// and with keys and lists in a stable order so two builds diff cleanly.
    /// </summary>
    public sealed class EcsGraph
    {
        public GraphMetadata Metadata = new GraphMetadata();
        public List<GraphNode> Nodes = new List<GraphNode>();
        public List<GraphEdge> Edges = new List<GraphEdge>();

        Dictionary<string, GraphNode>? _byId;

        public GraphNode? Node(string id)
        {
            _byId ??= Nodes.ToDictionary(n => n.Id, StringComparer.Ordinal);
            return _byId.TryGetValue(id, out var node) ? node : null;
        }

        public IEnumerable<GraphEdge> Outgoing(string id) => Edges.Where(e => e.Source == id);
        public IEnumerable<GraphEdge> Incoming(string id) => Edges.Where(e => e.Target == id);

        public void Sort()
        {
            Nodes.Sort((a, b) => string.CompareOrdinal(a.Id, b.Id));
            Edges.Sort((a, b) =>
            {
                var c = string.CompareOrdinal(a.Source, b.Source);
                if (c != 0) return c;
                c = string.CompareOrdinal(a.Target, b.Target);
                if (c != 0) return c;
                c = string.CompareOrdinal(a.Relation, b.Relation);
                if (c != 0) return c;
                c = a.Order.CompareTo(b.Order);
                return c != 0 ? c : string.CompareOrdinal(a.Key, b.Key);
            });
            _byId = null;
        }

        // -- JSON ------------------------------------------------------------------

        public string ToJson()
        {
            using var stream = new MemoryStream();
            // graph.json is read by people and scripts, never embedded in HTML: no need to escape + or <.
            using (var w = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = true, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping }))
            {
                w.WriteStartObject();
                w.WriteBoolean("directed", true);
                w.WriteBoolean("multigraph", true);
                w.WriteStartObject("graph");
                w.WriteNumber("schema_version", Metadata.SchemaVersion);
                w.WriteString("tool", Metadata.Tool);
                w.WriteString("tool_version", Metadata.ToolVersion);
                w.WriteString("project", Metadata.Project);
                if (Metadata.BuiltAtCommit != null) w.WriteString("built_at_commit", Metadata.BuiltAtCommit);
                w.WriteNumber("build_ms", Metadata.BuildMilliseconds);
                w.WriteNumber("source_files", Metadata.SourceFiles);
                w.WriteStartObject("conventions");
                w.WriteString("request_suffix", Metadata.RequestSuffix);
                w.WriteString("event_suffix", Metadata.EventSuffix);
                w.WriteEndObject();
                w.WriteStartArray("unresolved_destroys");
                foreach (var u in Metadata.UnresolvedDestroys.OrderBy(u => u.File, StringComparer.Ordinal).ThenBy(u => u.Line))
                {
                    w.WriteStartObject();
                    w.WriteString("source", u.Source);
                    w.WriteString("source_file", u.File);
                    w.WriteString("source_location", "L" + u.Line);
                    w.WriteEndObject();
                }
                w.WriteEndArray();
                w.WriteEndObject();

                w.WriteStartArray("nodes");
                foreach (var n in Nodes)
                {
                    w.WriteStartObject();
                    w.WriteString("id", n.Id);
                    w.WriteString("label", n.Label);
                    w.WriteString("kind", n.Kind);
                    if (n.FullName != null) w.WriteString("full_name", n.FullName);
                    if (n.SourceFile != null) w.WriteString("source_file", n.SourceFile);
                    if (n.Line > 0) w.WriteString("source_location", "L" + n.Line);
                    if (n.Generated) w.WriteBoolean("generated", true);
                    if (n.Kind == NodeKind.Component)
                    {
                        WriteStrings(w, "contexts", n.Contexts);
                        w.WriteBoolean("flag", n.Flag);
                        if (n.Fields.Count > 0) WriteStrings(w, "fields", n.Fields);
                        if (n.Attributes.Count > 0) WriteStrings(w, "attributes", n.Attributes);
                        if (n.Role != null) w.WriteString("role", n.Role);
                        if (n.TransientRemovedBy.Count > 0) WriteStrings(w, "transient_removed_by", n.TransientRemovedBy);
                    }
                    if (n.Interfaces.Count > 0) WriteStrings(w, "interfaces", n.Interfaces);
                    if (n.Root) w.WriteBoolean("root", true);
                    if (n.RegisteredIn.Count > 0)
                    {
                        w.WriteStartArray("registered_in");
                        foreach (var r in n.RegisteredIn)
                        {
                            w.WriteStartObject();
                            w.WriteString("feature", r.Feature);
                            w.WriteNumber("index", r.Index);
                            w.WriteEndObject();
                        }
                        w.WriteEndArray();
                    }
                    if (n.Placements.Count > 0)
                    {
                        w.WriteStartArray("placements");
                        foreach (var p in n.Placements)
                        {
                            w.WriteStartObject();
                            w.WriteString("root", p.Root);
                            w.WriteNumber("order", p.Order);
                            WriteStrings(w, "chain", p.Chain);
                            w.WriteEndObject();
                        }
                        w.WriteEndArray();
                    }
                    w.WriteEndObject();
                }
                w.WriteEndArray();

                w.WriteStartArray("links");
                foreach (var e in Edges)
                {
                    w.WriteStartObject();
                    w.WriteString("source", e.Source);
                    w.WriteString("target", e.Target);
                    w.WriteString("relation", e.Relation);
                    w.WriteString("via", e.Via);
                    w.WriteString("confidence", e.Confidence);
                    if (e.Context != null) w.WriteString("context", e.Context);
                    if (e.Through != null) w.WriteString("through", e.Through);
                    if (e.Called != null) w.WriteString("called", e.Called);
                    if (e.Entity != null) w.WriteString("entity", e.Entity);
                    if (e.OnlyWhile != null) w.WriteString("only_while", e.OnlyWhile);
                    if (e.Condition != null) w.WriteString("condition", e.Condition);
                    if (e.Order > 0) w.WriteNumber("order", e.Order);
                    if (e.Count > 1) w.WriteNumber("count", e.Count);
                    if (e.SourceFile != null) w.WriteString("source_file", e.SourceFile);
                    if (e.Line > 0) w.WriteString("source_location", "L" + e.Line);
                    w.WriteEndObject();
                }
                w.WriteEndArray();
                w.WriteEndObject();
            }

            return Encoding.UTF8.GetString(stream.ToArray()) + "\n";
        }

        static void WriteStrings(Utf8JsonWriter w, string name, IEnumerable<string> values)
        {
            w.WriteStartArray(name);
            foreach (var v in values) w.WriteStringValue(v);
            w.WriteEndArray();
        }

        public static EcsGraph FromJson(string json)
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            var graph = new EcsGraph();

            var meta = root.GetProperty("graph");
            graph.Metadata.SchemaVersion = meta.GetProperty("schema_version").GetInt32();
            graph.Metadata.ToolVersion = Str(meta, "tool_version") ?? string.Empty;
            graph.Metadata.Project = Str(meta, "project") ?? string.Empty;
            graph.Metadata.BuiltAtCommit = Str(meta, "built_at_commit");
            graph.Metadata.BuildMilliseconds = meta.TryGetProperty("build_ms", out var ms) ? ms.GetInt64() : 0;
            graph.Metadata.SourceFiles = meta.TryGetProperty("source_files", out var sf) ? sf.GetInt32() : 0;
            if (meta.TryGetProperty("conventions", out var conv))
            {
                graph.Metadata.RequestSuffix = Str(conv, "request_suffix") ?? "Request";
                graph.Metadata.EventSuffix = Str(conv, "event_suffix") ?? "Event";
            }
            if (meta.TryGetProperty("unresolved_destroys", out var unresolved))
                foreach (var u in unresolved.EnumerateArray())
                    graph.Metadata.UnresolvedDestroys.Add(new UnresolvedDestroy
                    {
                        Source = u.GetProperty("source").GetString()!,
                        File = Str(u, "source_file") ?? string.Empty,
                        Line = ParseLine(Str(u, "source_location"))
                    });

            foreach (var n in root.GetProperty("nodes").EnumerateArray())
            {
                var node = new GraphNode
                {
                    Id = n.GetProperty("id").GetString()!,
                    Label = n.GetProperty("label").GetString()!,
                    Kind = Str(n, "kind") ?? NodeKind.Type,
                    FullName = Str(n, "full_name"),
                    SourceFile = Str(n, "source_file"),
                    Line = ParseLine(Str(n, "source_location")),
                    Generated = Bool(n, "generated"),
                    Flag = Bool(n, "flag"),
                    Role = Str(n, "role"),
                    Root = Bool(n, "root")
                };
                node.Contexts.AddRange(Strs(n, "contexts"));
                node.Fields.AddRange(Strs(n, "fields"));
                node.Attributes.AddRange(Strs(n, "attributes"));
                node.TransientRemovedBy.AddRange(Strs(n, "transient_removed_by"));
                node.Interfaces.AddRange(Strs(n, "interfaces"));
                if (n.TryGetProperty("registered_in", out var reg))
                    foreach (var r in reg.EnumerateArray())
                        node.RegisteredIn.Add(new Registration { Feature = r.GetProperty("feature").GetString()!, Index = r.GetProperty("index").GetInt32() });
                if (n.TryGetProperty("placements", out var pl))
                    foreach (var p in pl.EnumerateArray())
                    {
                        var placement = new Placement { Root = p.GetProperty("root").GetString()!, Order = p.GetProperty("order").GetInt32() };
                        placement.Chain.AddRange(Strs(p, "chain"));
                        node.Placements.Add(placement);
                    }
                graph.Nodes.Add(node);
            }

            foreach (var e in root.GetProperty("links").EnumerateArray())
            {
                graph.Edges.Add(new GraphEdge
                {
                    Source = e.GetProperty("source").GetString()!,
                    Target = e.GetProperty("target").GetString()!,
                    Relation = e.GetProperty("relation").GetString()!,
                    Via = Str(e, "via") ?? string.Empty,
                    Confidence = Str(e, "confidence") ?? Entitas.Graph.Confidence.Extracted,
                    Context = Str(e, "context"),
                    Through = Str(e, "through"),
                    Called = Str(e, "called"),
                    Entity = Str(e, "entity"),
                    OnlyWhile = Str(e, "only_while"),
                    Condition = Str(e, "condition"),
                    Order = e.TryGetProperty("order", out var o) ? o.GetInt32() : 0,
                    Count = e.TryGetProperty("count", out var c) ? c.GetInt32() : 1,
                    SourceFile = Str(e, "source_file"),
                    Line = ParseLine(Str(e, "source_location"))
                });
            }

            return graph;
        }

        static string? Str(JsonElement e, string name) =>
            e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

        static bool Bool(JsonElement e, string name) =>
            e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.True;

        static IEnumerable<string> Strs(JsonElement e, string name) =>
            e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Array
                ? v.EnumerateArray().Select(x => x.GetString()!).ToArray()
                : Array.Empty<string>();

        static int ParseLine(string? location) =>
            location != null && location.StartsWith("L", StringComparison.Ordinal) && int.TryParse(location.Substring(1), out var line) ? line : 0;
    }
}
