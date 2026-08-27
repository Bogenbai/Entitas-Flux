using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using Entitas.SourceGenerator.CodeGeneration;
using Entitas.SourceGenerator.Discovery;
using Microsoft.CodeAnalysis;

namespace Entitas.SourceGenerator
{
    /// <summary>
    /// Everything generation depends on, extracted from a <see cref="Compilation"/> and
    /// compared BY VALUE.
    ///
    /// This is what makes the incremental generator incremental: Roslyn re-runs the
    /// extraction on every keystroke, but only re-runs generation when the extracted
    /// value differs. Editing a method body, a comment or an unrelated class produces an
    /// equal input, so the ~25 generators and every AddSource are skipped entirely.
    ///
    /// It must therefore hold no symbols, syntax nodes or anything else rooted in a
    /// compilation — only strings, bools and the plain data POCOs.
    /// </summary>
    public sealed class GenerationInput : IEquatable<GenerationInput>
    {
        public static readonly GenerationInput Disabled =
            new GenerationInput(false, new string[0], DefaultOptions(), new CodeGeneratorData[0], null);

        /// <summary>False when the assembly did not opt in (no Entitas reference, or no [ContextDefinition]).</summary>
        public bool Enabled { get; }

        public string[] ContextNames { get; }
        public EntitasGenerationOptions Options { get; }
        public CodeGeneratorData[] Data { get; }

        /// <summary>Set when discovery itself threw; reported as ENT0101 instead of crashing the compiler.</summary>
        public string? DiscoveryError { get; }

        public GenerationInput(
            bool enabled,
            string[] contextNames,
            EntitasGenerationOptions options,
            CodeGeneratorData[] data,
            string? discoveryError)
        {
            Enabled = enabled;
            ContextNames = contextNames;
            Options = options;
            Data = data;
            DiscoveryError = discoveryError;
        }

        static EntitasGenerationOptions DefaultOptions() =>
            new EntitasGenerationOptions(EntityApiStyle.Plain, false, false, new string[0]);

        public static GenerationInput From(Compilation compilation)
        {
            // Opt-in guard: must reference Entitas AND declare at least one context.
            if (compilation.GetTypeByMetadataName(WellKnownTypes.ComponentInterface) is null)
                return Disabled;

            var resolver = ContextResolver.FromCompilation(compilation);
            if (resolver.ContextNames.Length == 0)
                return Disabled;

            var options = EntitasGenerators.ReadOptions(compilation);

            try
            {
                // Discovery reads this static (faithful to the legacy plugin).
                CodeGeneratorExtensions.ignoreNamespaces = options.IgnoreNamespaces;

                var types = EntitasDiscovery.GetCandidateTypes(compilation).ToArray();
                var result = EntitasDiscovery.Discover(types, resolver, options.IgnoreNamespaces);

                var data = new List<CodeGeneratorData>();
                data.AddRange(result.Components);
                data.AddRange(result.Contexts);
                data.AddRange(result.EntityIndices);
                data.AddRange(result.Cleanups);
                data.AddRange(result.WatchedCleanups);

                return new GenerationInput(true, resolver.ContextNames, options, data.ToArray(), null);
            }
            catch (Exception exception)
            {
                return new GenerationInput(
                    false, resolver.ContextNames, options, new CodeGeneratorData[0], Describe(exception));
            }
        }

        internal static string Describe(Exception exception) =>
            $"{exception.GetType().Name}: {exception.Message}";

        // -- value equality ---------------------------------------------------

        public bool Equals(GenerationInput? other)
        {
            if (ReferenceEquals(this, other))
                return true;
            if (other is null)
                return false;

            return Enabled == other.Enabled &&
                   string.Equals(DiscoveryError, other.DiscoveryError, StringComparison.Ordinal) &&
                   ContextNames.SequenceEqual(other.ContextNames, StringComparer.Ordinal) &&
                   Options.Equals(other.Options) &&
                   DataEquals(Data, other.Data);
        }

        public override bool Equals(object? obj) => Equals(obj as GenerationInput);

        public override int GetHashCode()
        {
            var hash = Enabled ? 17 : 23;
            hash = Combine(hash, Options.GetHashCode());
            foreach (var name in ContextNames)
                hash = Combine(hash, StringComparer.Ordinal.GetHashCode(name));

            // The data array is large; its length plus each entry's key count is a cheap
            // and stable discriminator — Equals does the exact comparison.
            hash = Combine(hash, Data.Length);
            foreach (var data in Data)
                hash = Combine(hash, data.Count);

            return hash;
        }

        static int Combine(int hash, int value) => unchecked(hash * 31 + value);

        static bool DataEquals(CodeGeneratorData[] left, CodeGeneratorData[] right)
        {
            if (left.Length != right.Length)
                return false;

            for (var i = 0; i < left.Length; i++)
            {
                if (!DataEquals(left[i], right[i]))
                    return false;
            }

            return true;
        }

        static bool DataEquals(CodeGeneratorData left, CodeGeneratorData right)
        {
            if (left.GetType() != right.GetType() || left.Count != right.Count)
                return false;

            foreach (var entry in left)
            {
                if (!right.TryGetValue(entry.Key, out var other) || !ValueEquals(entry.Value, other))
                    return false;
            }

            return true;
        }

        /// <summary>
        /// Data values are strings, bools, enums and arrays of the plain POCOs, so a
        /// recursive elementwise comparison covers every case the providers produce.
        /// </summary>
        static bool ValueEquals(object? left, object? right)
        {
            if (ReferenceEquals(left, right))
                return true;
            if (left is null || right is null)
                return false;

            if (left is string leftString && right is string rightString)
                return string.Equals(leftString, rightString, StringComparison.Ordinal);

            if (left is Array leftArray && right is Array rightArray)
            {
                if (leftArray.Length != rightArray.Length)
                    return false;

                var leftItems = (IList)leftArray;
                var rightItems = (IList)rightArray;
                for (var i = 0; i < leftItems.Count; i++)
                {
                    if (!ValueEquals(leftItems[i], rightItems[i]))
                        return false;
                }

                return true;
            }

            if (left is CodeGeneratorData leftData && right is CodeGeneratorData rightData)
                return DataEquals(leftData, rightData);

            return left.Equals(right);
        }
    }

    /// <summary>Generated sources plus anything the run wants to tell the user about.</summary>
    public sealed class GenerationResult
    {
        public static readonly GenerationResult Empty =
            new GenerationResult(new GeneratedSource[0], new Diagnostic[0]);

        public IReadOnlyList<GeneratedSource> Sources { get; }
        public IReadOnlyList<Diagnostic> Diagnostics { get; }

        public GenerationResult(IReadOnlyList<GeneratedSource> sources, IReadOnlyList<Diagnostic> diagnostics)
        {
            Sources = sources;
            Diagnostics = diagnostics;
        }
    }

    /// <summary>
    /// Diagnostics the generation pipeline reports. Generation used to swallow every
    /// exception, which surfaced as hundreds of unexplained CS1061s in user code — a
    /// failure now says which generator failed and why.
    /// </summary>
    public static class EntitasDiagnostics
    {
        public const string GeneratorFailedId = "ENT0100";
        public const string DiscoveryFailedId = "ENT0101";

        static readonly DiagnosticDescriptor GeneratorFailedRule = new DiagnosticDescriptor(
            GeneratorFailedId,
            "Entitas generator failed",
            "The Entitas generator '{0}' failed and produced no code: {1}. The API it generates will be missing.",
            "Entitas",
            DiagnosticSeverity.Error,
            isEnabledByDefault: true);

        static readonly DiagnosticDescriptor DiscoveryFailedRule = new DiagnosticDescriptor(
            DiscoveryFailedId,
            "Entitas discovery failed",
            "Entitas could not build its data model from this assembly: {0}. No code was generated.",
            "Entitas",
            DiagnosticSeverity.Error,
            isEnabledByDefault: true);

        public static Diagnostic GeneratorFailed(string generatorName, string reason) =>
            Diagnostic.Create(GeneratorFailedRule, Location.None, generatorName, reason);

        public static Diagnostic DiscoveryFailed(string reason) =>
            Diagnostic.Create(DiscoveryFailedRule, Location.None, reason);
    }
}
