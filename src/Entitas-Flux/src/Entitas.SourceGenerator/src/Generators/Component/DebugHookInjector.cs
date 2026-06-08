using System;

namespace Entitas.SourceGenerator.CodeGeneration
{
    /// <summary>
    /// When [assembly: EntitasGeneration(DebugHooks = true)] is set, rewrites a generated
    /// entity-API file to call empty <c>partial void OnAdd{X}/OnReplace{X}</c> hooks at the top
    /// of each Add/Replace and to declare them. Implement a hook in your own partial of the
    /// entity to breakpoint a specific mutation (an unimplemented partial method is elided by
    /// the compiler, so it costs nothing when unused).
    ///
    /// Runs ONLY when DebugHooks is on, so the default output — and the frozen golden baseline —
    /// stay byte-for-byte unchanged.
    /// </summary>
    public static class DebugHookInjector
    {
        public static string Inject(string content, ComponentData data, string contextName)
        {
            var members = data.GetMemberData();
            if (members.Length == 0)
                return content; // flag components have no Add/Replace methods

            var entityType = contextName.AddEntitySuffix();
            var name = data.ComponentName();
            var methodParams = members.GetMethodParameters(true);
            var args = CodeGeneratorExtensions.GetMethodArgs(members, true);

            content = InsertAfterHeader(content, $"public {entityType} Add{name}({methodParams}) {{",
                $"        OnAdd{name}({args});");
            content = InsertAfterHeader(content, $"public {entityType} Replace{name}({methodParams}) {{",
                $"        OnReplace{name}({args});");

            var declarations =
                $"\n    partial void OnAdd{name}({methodParams});" +
                $"\n    partial void OnReplace{name}({methodParams});\n";

            var lastBrace = content.LastIndexOf('}');
            return lastBrace < 0
                ? content
                : content.Substring(0, lastBrace) + declarations + content.Substring(lastBrace);
        }

        static string InsertAfterHeader(string content, string header, string insertion)
        {
            var idx = content.IndexOf(header, StringComparison.Ordinal);
            if (idx < 0)
                return content;

            var at = idx + header.Length;
            return content.Substring(0, at) + "\n" + insertion + content.Substring(at);
        }
    }
}
