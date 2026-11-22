namespace Entitas.CodeGeneration.Attributes
{
    /// <summary>
    /// Selects the entity-API generation style emitted by the Entitas-Flux source
    /// generator. <see cref="Plain"/> mirrors the canonical default (entity.foo.value
    /// plus Add/Replace/Remove); <see cref="Atomic"/> emits the single-field atomic
    /// accessor (e.g. <c>entity.CurrentHealth</c>) for components with a single
    /// <c>Value</c> member.
    /// </summary>
    public enum EntityApiStyle { Plain = 0, Atomic = 1 }

    /// <summary>
    /// Selects how the Unity visual debugging observes contexts.
    /// <see cref="EntityGameObjects"/> mirrors the canonical default: one GameObject per
    /// context, plus one child GameObject per entity. <see cref="SingleGameObject"/> emits
    /// a single "Entitas Debug" GameObject for all contexts and browses their entities from
    /// its inspector instead, which keeps the hierarchy (and the editor) fast with a large
    /// number of entities.
    /// </summary>
    public enum VisualDebuggingStyle { EntityGameObjects = 0, SingleGameObject = 1 }

    /// <summary>
    /// Where the generated ECS API lives.
    /// <see cref="SourceGenerator"/> (the default) generates it inside the compiler on every
    /// build; nothing is written to disk. <see cref="Files"/> writes it to ordinary .cs files
    /// under <see cref="EntitasGenerationAttribute.OutputPath"/> — regenerated from the Unity
    /// editor (Tools > Entitas > Generate, or automatically when a component changes) or
    /// with the <c>entitas-gen</c> CLI — and the source generator stands down so nothing is
    /// defined twice. Both produce the same code.
    /// </summary>
    public enum GenerationOutput { SourceGenerator = 0, Files = 1 }

    /// <summary>
    /// Assembly-level configuration for the Entitas-Flux source generator. Place a
    /// single instance on the assembly that owns the components to customize
    /// generation without rebuilding the framework. Read by the generator via
    /// string-based metadata lookup (no runtime dependency on this assembly).
    /// </summary>
    [System.AttributeUsage(System.AttributeTargets.Assembly, AllowMultiple = false)]
    public sealed class EntitasGenerationAttribute : System.Attribute
    {
        public EntityApiStyle EntityApi { get; set; } = EntityApiStyle.Plain;
        public bool IgnoreNamespaces { get; set; } = false;

        /// <summary>
        /// When true, the entity-API generators emit empty <c>partial void OnAdd{X}/OnReplace{X}</c>
        /// hook methods and call them at the top of each Add/Replace. Implement a hook in your own
        /// partial of the entity to set a breakpoint on a specific mutation (an unimplemented hook is
        /// removed by the compiler, so this is zero-cost when you don't use it). For debugging only.
        /// </summary>
        public bool DebugHooks { get; set; } = false;

        public VisualDebuggingStyle VisualDebugging { get; set; } = VisualDebuggingStyle.EntityGameObjects;

        /// <summary>
        /// <see cref="GenerationOutput.Files"/> writes the generated code to .cs files instead
        /// of generating it inside the compiler. See <see cref="GenerationOutput"/>.
        /// </summary>
        public GenerationOutput Output { get; set; } = GenerationOutput.SourceGenerator;

        /// <summary>
        /// Folder for <see cref="GenerationOutput.Files"/>, relative to the Unity project root
        /// (e.g. <c>"Assets/Code/Generated"</c>). It must belong to this assembly — inside the
        /// same asmdef folder — or the files would compile into another one. When empty, a
        /// <c>Generated</c> folder next to the file declaring the first
        /// <c>[assembly: ContextDefinition]</c> is used, which always does.
        /// </summary>
        public string OutputPath { get; set; } = "";
    }

    /// <summary>
    /// Disables a built-in Entitas-Flux generator by name. May be applied multiple
    /// times. The supplied string matches a generator's CLASS short name (e.g.
    /// "ContextObserverGenerator") case-insensitively, OR as a prefix (so "Event"
    /// disables all Event* generators, "Watched" disables all Watched*).
    /// </summary>
    [System.AttributeUsage(System.AttributeTargets.Assembly, AllowMultiple = true)]
    public sealed class DisableEntitasGeneratorAttribute : System.Attribute
    {
        public readonly string generator;

        public DisableEntitasGeneratorAttribute(string generator)
        {
            this.generator = generator;
        }
    }
}
