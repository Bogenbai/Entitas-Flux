using System;

namespace Entitas
{
    /// <summary>
    /// Debug hooks fired from generated entity Add/Replace methods when an assembly opts in
    /// with <c>[assembly: EntitasGeneration(DebugHooks = true)]</c>. Assign a handler (e.g. in
    /// a bootstrap) and set a breakpoint inside it to catch a specific mutation — this replaces
    /// the old "edit the generated ReplaceX and add an `if`" workflow without editing generated
    /// code. Handlers are null by default, so unused hooks cost a single null-check. Debug only.
    ///
    /// This type lives in the runtime (not generated), so the IDE always resolves it.
    /// <c>newValue</c> is the incoming value for single-field components, otherwise null
    /// (read the rest from the entity).
    /// </summary>
    public static class EntitasDebugHooks
    {
        public static Action<IEntity, int, object> OnAdd;
        public static Action<IEntity, int, object> OnReplace;
    }
}
