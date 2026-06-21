using System;

namespace Entitas.SourceGenerator.CodeGeneration
{
    /// <summary>
    /// When [assembly: EntitasGeneration(DebugHooks = true)] is set, rewrites a generated
    /// entity-API file to call the runtime <see cref="Entitas.EntitasDebugHooks"/> delegates at
    /// the top of each Add/Replace. Assign a handler and breakpoint inside it to catch a specific
    /// mutation — no partial methods (so the IDE never reports an orphaned implementation), and
    /// the hook type lives in the runtime so it always resolves. A null handler costs one
    /// null-check.
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
            var index = contextName + CodeGeneratorExtensions.LOOKUP + "." + name;
            // The incoming value for single-field (atomic) components; otherwise null.
            var value = members.IsAtomicComponent() ? "newValue" : "null";

            content = InsertAfterHeader(content, $"public {entityType} Add{name}({methodParams}) {{",
                $"        Entitas.EntitasDebugHooks.OnAdd?.Invoke(this, {index}, {value});");
            content = InsertAfterHeader(content, $"public {entityType} Replace{name}({methodParams}) {{",
                $"        Entitas.EntitasDebugHooks.OnReplace?.Invoke(this, {index}, {value});");

            return content;
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
