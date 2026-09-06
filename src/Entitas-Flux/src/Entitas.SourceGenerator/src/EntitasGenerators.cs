using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Entitas.SourceGenerator.CodeGeneration;
using Entitas.SourceGenerator.Discovery;
using Microsoft.CodeAnalysis;

namespace Entitas.SourceGenerator
{
    /// <summary>A merged generated source: a logical file path plus its content (no header).</summary>
    public readonly struct GeneratedSource
    {
        /// <summary>Logical path, e.g. "Game/Components/GamePositionComponent.cs".</summary>
        public readonly string FileName;

        /// <summary>Merged content for that file (the auto-generated header is added by the caller).</summary>
        public readonly string Content;

        public GeneratedSource(string fileName, string content)
        {
            FileName = fileName;
            Content = content;
        }
    }
    /// <summary>
    /// Public, reusable façade over the Entitas-Flux generator set. Power-users that
    /// reference Entitas.SourceGenerator.dll from their own analyzer can call
    /// <see cref="All"/> to get the raw canonical set and compose it with their own
    /// <see cref="AbstractGenerator"/>s, or <see cref="Default"/> to get the set the
    /// framework itself runs (already adjusted for the assembly's config attributes).
    ///
    /// Config is read from the compilation's assembly attributes by full metadata
    /// name (no reference to the attributes assembly is taken), consistent with how
    /// <c>ContextDefinitionAttribute</c> is read:
    ///   - <c>[assembly: EntitasGeneration(EntityApi = EntityApiStyle.Atomic)]</c>
    ///     swaps <see cref="ComponentEntityApiGenerator"/> for
    ///     <see cref="AtomicComponentEntityApiGenerator"/> (they conflict; one or the
    ///     other, never both).
    ///   - <c>[assembly: EntitasGeneration(IgnoreNamespaces = true)]</c> is surfaced
    ///     via <see cref="ReadOptions"/> and applied by the host before discovery.
    ///   - <c>[assembly: DisableEntitasGenerator("X")]</c> removes any built-in
    ///     generator whose CLASS short name matches "X" case-insensitively, OR starts
    ///     with "X" (prefix match: "Event" disables all Event* generators, "Watched"
    ///     disables all Watched*, "ContextObserver" disables just that one).
    /// </summary>
    public static class EntitasGenerators
    {
        /// <summary>
        /// Runs the full Entitas-Flux generation for a compilation and returns the merged
        /// sources (one entry per logical file, fragments sharing a FileName concatenated).
        /// This is the single source of truth shared by the incremental generator (which
        /// AddSources each entry) and the standalone CLI (which writes each entry to disk).
        ///
        /// Returns empty for assemblies that did not opt in (no Entitas reference, or no
        /// [assembly: ContextDefinition]). The returned Content has no auto-generated
        /// header — the caller adds whichever header it wants.
        /// </summary>
        public static IReadOnlyList<GeneratedSource> Generate(Compilation compilation) =>
            Generate(GenerationInput.From(compilation)).Sources;

        /// <summary>
        /// Runs generation over an already-extracted <see cref="GenerationInput"/>. The
        /// incremental generator calls this so the expensive half of the pipeline runs
        /// only when the input actually changed; the CLIs go through the Compilation
        /// overload above.
        /// </summary>
        public static GenerationResult Generate(GenerationInput input) =>
            Generate(input, null);

        /// <summary>
        /// Same, with an explicit generator set — the config-resolved default when null.
        /// Power-users composing their own set can reuse the whole pipeline this way.
        /// </summary>
        public static GenerationResult Generate(GenerationInput input, IReadOnlyList<AbstractGenerator>? generators)
        {
            if (input.DiscoveryError != null)
            {
                return new GenerationResult(
                    new GeneratedSource[0],
                    new[] { EntitasDiagnostics.DiscoveryFailed(input.DiscoveryError) });
            }

            if (!input.Enabled)
                return GenerationResult.Empty;

            CodeGeneratorExtensions.ignoreNamespaces = input.Options.IgnoreNamespaces;
            CodeGeneratorExtensions.debugHooks = input.Options.DebugHooks;

            generators ??= Default(input.Options);
            var dataArray = input.Data;
            var diagnostics = new List<Diagnostic>();

            // Merge fragments by logical FileName (reproduces the legacy Jenny layout),
            // preserving first-seen order for deterministic output.
            var byFile = new Dictionary<string, List<string>>();
            var order = new List<string>();
            foreach (var generator in generators)
            {
                CodeGenFile[] files;
                try
                {
                    files = generator.Generate(dataArray);
                }
                catch (Exception exception)
                {
                    // One misbehaving generator must not abort the whole run — but it
                    // must not vanish silently either: the API it owns will be missing,
                    // and without this the user only sees the fallout in their own code.
                    diagnostics.Add(EntitasDiagnostics.GeneratorFailed(
                        generator.GetType().Name, GenerationInput.Describe(exception)));
                    continue;
                }

                foreach (var file in files)
                {
                    if (file == null || string.IsNullOrEmpty(file.FileContent))
                        continue;

                    if (!byFile.TryGetValue(file.FileName, out var fragments))
                    {
                        fragments = new List<string>();
                        byFile.Add(file.FileName, fragments);
                        order.Add(file.FileName);
                    }

                    fragments.Add(file.FileContent);
                }
            }

            var output = new List<GeneratedSource>(order.Count);
            foreach (var fileName in order)
            {
                var sb = new StringBuilder();
                foreach (var fragment in byFile[fileName])
                {
                    sb.Append(fragment);
                    if (!fragment.EndsWith("\n"))
                        sb.Append('\n');
                    sb.Append('\n');
                }

                output.Add(new GeneratedSource(fileName, sb.ToString()));
            }

            return new GenerationResult(output, diagnostics);
        }

        /// <summary>
        /// The raw canonical default set, ignoring all config attributes. Uses the
        /// plain <see cref="ComponentEntityApiGenerator"/> (NOT atomic). Returns fresh
        /// instances each call so power-users can freely compose/mutate the list.
        /// </summary>
        public static IReadOnlyList<AbstractGenerator> All() => new AbstractGenerator[]
        {
            new ComponentContextApiGenerator(),
            new ComponentEntityApiGenerator(),
            new ComponentEntityApiInterfaceGenerator(),
            new ComponentGenerator(),
            new ComponentLookupGenerator(),
            new ComponentMatcherApiGenerator(),
            new WatchedComponentGenerator(),
            new ContextAttributeGenerator(),
            new ContextGenerator(),
            new ContextMatcherGenerator(),
            new ContextsGenerator(),
            new EntityGenerator(),
            new EntityIndexGenerator(),
            new EventEntityApiGenerator(),
            new EventListenerComponentGenerator(),
            new EventListenerInterfaceGenerator(),
            new EventSystemGenerator(),
            new EventSystemsGenerator(),
            new ContextObserverGenerator(),
            new FeatureClassGenerator(),
            new CleanupSystemGenerator(),
            new CleanupSystemsGenerator(),
            new WatchedEntityHasChangedGenerator(),
            new WatchedCleanupSystemGenerator(),
            new WatchedCleanupSystemsGenerator(),
        };

        /// <summary>
        /// The canonical default set, already adjusted for the assembly's config
        /// attributes (EntityApi swap + DisableEntitasGenerator removals). This is what
        /// <see cref="EntitasIncrementalGenerator"/> runs. <see cref="ReadOptions"/>
        /// returns the remaining (non-generator-list) options such as IgnoreNamespaces.
        /// </summary>
        public static IReadOnlyList<AbstractGenerator> Default(Compilation compilation) =>
            Default(ReadOptions(compilation));

        /// <summary>Same, for callers that already resolved the options.</summary>
        public static IReadOnlyList<AbstractGenerator> Default(EntitasGenerationOptions options)
        {
            var generators = All().ToList();

            // EntityApi == Atomic: swap the plain entity-API generator for the atomic
            // one in place (they emit conflicting entity members, so never both), and
            // drop the multi-context entity-interface generator. The interface declares
            // the plain lowercase component accessor (e.g. `cinematicTypeId`), which the
            // atomic API hides as a private member under ENTITAS_HIDE_STANDARD_MEMBERS —
            // the entity then cannot implement the interface (CS0737). The canonical
            // atomic config omits the interface generator for this reason.
            if (options.EntityApi == EntityApiStyle.Atomic)
            {
                for (var i = 0; i < generators.Count; i++)
                {
                    if (generators[i] is ComponentEntityApiGenerator)
                    {
                        generators[i] = new AtomicComponentEntityApiGenerator();
                        break;
                    }
                }

                generators.RemoveAll(g => g is ComponentEntityApiInterfaceGenerator);
            }

            if (options.VisualDebugging == VisualDebuggingStyle.SingleGameObject)
            {
                for (var i = 0; i < generators.Count; i++)
                {
                    if (generators[i] is ContextObserverGenerator)
                    {
                        generators[i] = new ContextObserverSingleObjectGenerator();
                        break;
                    }
                }
            }

            // DisableEntitasGenerator: drop matching built-ins (case-insensitive exact
            // short-name OR prefix match — see ReadOptions doc).
            if (options.DisabledGenerators.Count > 0)
            {
                generators.RemoveAll(g => IsDisabled(g, options.DisabledGenerators));
            }

            return generators;
        }

        static bool IsDisabled(AbstractGenerator generator, IReadOnlyList<string> disabled)
        {
            var shortName = generator.GetType().Name;
            foreach (var token in disabled)
            {
                if (string.IsNullOrEmpty(token))
                    continue;
                if (shortName.Equals(token, StringComparison.OrdinalIgnoreCase))
                    return true;
                if (shortName.StartsWith(token, StringComparison.OrdinalIgnoreCase))
                    return true;
            }

            return false;
        }

        /// <summary>
        /// Reads the Entitas-Flux generation config from the compilation's assembly
        /// attributes by full metadata name. Enum named-arguments arrive boxed as ints.
        /// </summary>
        public static EntitasGenerationOptions ReadOptions(Compilation compilation)
        {
            var entityApi = EntityApiStyle.Plain;
            var ignoreNamespaces = false;
            var debugHooks = false;
            var visualDebugging = VisualDebuggingStyle.EntityGameObjects;
            var disabled = new List<string>();

            foreach (var attr in compilation.Assembly.GetAttributes())
            {
                var name = attr.AttributeClass?.ToCompilableString();
                if (name == AttributeNames.EntitasGeneration)
                {
                    foreach (var named in attr.NamedArguments)
                    {
                        if (named.Key == "EntityApi" && named.Value.Value is int apiInt)
                            entityApi = (EntityApiStyle)apiInt;
                        else if (named.Key == "IgnoreNamespaces" && named.Value.Value is bool ignore)
                            ignoreNamespaces = ignore;
                        else if (named.Key == "DebugHooks" && named.Value.Value is bool hooks)
                            debugHooks = hooks;
                        else if (named.Key == "VisualDebugging" && named.Value.Value is int visualDebuggingInt)
                            visualDebugging = (VisualDebuggingStyle)visualDebuggingInt;
                    }
                }
                else if (name == AttributeNames.DisableEntitasGenerator)
                {
                    var arg = attr.ConstructorArguments.FirstOrDefault().Value as string;
                    if (!string.IsNullOrEmpty(arg))
                        disabled.Add(arg!);
                }
            }

            return new EntitasGenerationOptions(entityApi, ignoreNamespaces, debugHooks, visualDebugging, disabled);
        }
    }

    /// <summary>
    /// Mirror of the local <c>EntityApiStyle</c> enum in the attributes assembly.
    /// Kept here so the generator project stays dependency-free.
    /// </summary>
    public enum EntityApiStyle { Plain = 0, Atomic = 1 }

    /// <summary>
    /// Mirror of the local <c>VisualDebuggingStyle</c> enum in the attributes assembly.
    /// </summary>
    public enum VisualDebuggingStyle { EntityGameObjects = 0, SingleGameObject = 1 }

    /// <summary>Resolved Entitas-Flux generation config for a single compilation.</summary>
    public sealed class EntitasGenerationOptions : IEquatable<EntitasGenerationOptions>
    {
        public EntityApiStyle EntityApi { get; }
        public bool IgnoreNamespaces { get; }
        public bool DebugHooks { get; }
        public VisualDebuggingStyle VisualDebugging { get; }
        public IReadOnlyList<string> DisabledGenerators { get; }

        public EntitasGenerationOptions(
            EntityApiStyle entityApi,
            bool ignoreNamespaces,
            bool debugHooks,
            VisualDebuggingStyle visualDebugging,
            IReadOnlyList<string> disabledGenerators)
        {
            EntityApi = entityApi;
            IgnoreNamespaces = ignoreNamespaces;
            DebugHooks = debugHooks;
            VisualDebugging = visualDebugging;
            DisabledGenerators = disabledGenerators;
        }

        // Compared by value so GenerationInput can be: a config change must invalidate
        // the incremental cache, an unrelated edit must not.
        public bool Equals(EntitasGenerationOptions? other) =>
            other != null &&
            EntityApi == other.EntityApi &&
            IgnoreNamespaces == other.IgnoreNamespaces &&
            DebugHooks == other.DebugHooks &&
            VisualDebugging == other.VisualDebugging &&
            DisabledGenerators.SequenceEqual(other.DisabledGenerators, StringComparer.Ordinal);

        public override bool Equals(object? obj) => Equals(obj as EntitasGenerationOptions);

        public override int GetHashCode()
        {
            var hash = ((int)EntityApi * 397) ^ (IgnoreNamespaces ? 1 : 0) ^ (DebugHooks ? 2 : 0) ^ ((int)VisualDebugging * 4099);
            foreach (var name in DisabledGenerators)
                hash = unchecked(hash * 31 + StringComparer.Ordinal.GetHashCode(name));

            return hash;
        }
    }
}
