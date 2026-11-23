using System.Linq;
using System.Text;

namespace Entitas.Graph.Output
{
    /// <summary>What a name that matches several nodes, or none, is answered with — on the command line and as an MCP tool's error.</summary>
    public static class NameErrors
    {
        public static string Ambiguous(AmbiguousNameException ex)
        {
            var text = new StringBuilder($"{ex.Message} — be more specific:\n");
            foreach (var node in ex.Candidates.OrderBy(n => n.Label))
                text.Append($"  {node.FullName ?? node.Id}  [{node.Kind}]\n");
            return text.ToString();
        }

        public static string NotFound(NodeNotFoundException ex)
        {
            var text = new StringBuilder(ex.Message + (ex.Suggestions.Count > 0 ? ". Did you mean:\n" : ".\n"));
            foreach (var node in ex.Suggestions)
                text.Append($"  {node.Label}  [{node.Kind}]\n");
            return text.ToString();
        }
    }
}
