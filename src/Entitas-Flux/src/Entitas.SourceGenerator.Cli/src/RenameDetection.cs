using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Entitas.SourceGenerator.CodeGeneration;
using Entitas.SourceGenerator.Discovery;
using Entitas.SourceGenerator.Rename;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Entitas.SourceGenerator.Cli
{
    /// <summary>
    /// Recognises a component renamed in the IDE (Shift+F6 on the class) from the generated
    /// files it leaves behind, while they still exist (file mode).
    ///
    /// The IDE renames the class and every reference to the type — inside the generated
    /// files too — but not the members the generator derives from its name. So right before
    /// regeneration deletes them, the old component's files still declare <c>AddHealth</c>,
    /// <c>hasHealth</c>, <c>GameMatcher.Health</c>, … yet already reference
    /// <c>HpComponent</c>. That is the signal: a file about to be deleted that references a
    /// component which exists now.
    ///
    /// A signal is not proof, so the hypothesis "HpComponent was called HealthComponent" is
    /// checked by generating as if it still were: it must reproduce exactly the generated
    /// files on disk — every one about to be deleted, none of the ones about to be written.
    /// That generation swaps the name back in the component's discovery snapshot rather than
    /// in a new compilation: the compilation is already bound, so it costs ~15 ms of
    /// snapshotting on a ~2800-file project instead of ~250 ms of rebinding.
    /// Anything else (two components renamed at once, a rename plus a deleted or added
    /// component) is ambiguous and reported, never acted on.
    ///
    /// Only ever runs when generation is about to delete files, so a regeneration that adds
    /// or edits components costs nothing extra.
    /// </summary>
    public static class RenameDetector
    {
        /// <param name="sources">What generation produces now.</param>
        /// <param name="outputDirectory">Where <paramref name="sources"/> are written.</param>
        /// <param name="existing">Generated files present now (full paths).</param>
        public static RenameDetection Detect(
            Compilation compilation,
            GenerationInput input,
            IReadOnlyList<GeneratedSource> sources,
            string outputDirectory,
            IReadOnlyCollection<string> existing)
        {
            var comparer = GeneratedFileWriter.PathComparer;
            var desired = new HashSet<string>(
                sources.Select(source => GeneratedFileWriter.Combine(outputDirectory, source.FileName)), comparer);
            var present = new HashSet<string>(existing.Select(Path.GetFullPath), comparer);

            var stale = present.Where(path => !desired.Contains(path)).ToList();
            if (stale.Count == 0)
                return RenameDetection.None;

            var staleSet = new HashSet<string>(stale, comparer);
            var staleTrees = GeneratedFiles.FindGeneratedTrees(compilation)
                .Where(tree => !string.IsNullOrEmpty(tree.FilePath) && staleSet.Contains(Path.GetFullPath(tree.FilePath)))
                .ToList();
            if (staleTrees.Count == 0)
                return RenameDetection.None;

            var referenced = ReferencedComponents(compilation, input, staleTrees);
            if (referenced.Count == 0)
                return RenameDetection.None;

            var added = desired.Where(path => !present.Contains(path)).ToList();
            var ignoreNamespaces = input.Options.IgnoreNamespaces;

            var hypotheses = new List<(INamedTypeSymbol Component, string OldClassName)>();
            foreach (var pair in referenced)
            {
                foreach (var oldClassName in OldClassNames(pair.Key, pair.Value, added, ignoreNamespaces))
                    hypotheses.Add((pair.Key, oldClassName));
            }

            if (hypotheses.Count == 0)
                return RenameDetection.None;

            var config = GenerationConfig.From(compilation);
            var confirmed = new List<(INamedTypeSymbol Component, string OldClassName, IReadOnlyList<GeneratedSource> OldSources)>();
            foreach (var (component, oldClassName) in hypotheses)
            {
                var oldSources = GenerateAsIfNamed(compilation, config, component, oldClassName);
                if (oldSources == null)
                    continue;

                var reproduced = new HashSet<string>(
                    oldSources.Select(source => GeneratedFileWriter.Combine(outputDirectory, source.FileName)), comparer);
                if (reproduced.SetEquals(present))
                    confirmed.Add((component, oldClassName, oldSources));
            }

            if (confirmed.Count != 1)
            {
                var guesses = hypotheses
                    .Select(h => $"{h.OldClassName} -> {h.Component.Name}")
                    .Distinct(StringComparer.Ordinal)
                    .OrderBy(text => text, StringComparer.Ordinal);
                return new RenameDetection(null,
                    $"the generated files being deleted look like a component renamed in the IDE ({string.Join(", ", guesses)}), " +
                    "but other components were added, removed or renamed in the same generation, so which old names belong " +
                    "to which component is ambiguous. Usages of the old generated API were NOT updated. Rename one component " +
                    "at a time and regenerate in between, or use [RenameTo] / Tools > Entitas > Rename Component….");
            }

            var match = confirmed[0];
            var plan = RenameEngine.CreatePlanForRenamedComponent(
                compilation, match.Component, match.OldClassName, match.OldSources, sources);

            // The derived API is already renamed in the files on disk: a rename tool carried the
            // rename through, and only regeneration is left.
            if (plan == null)
                return RenameDetection.None;

            return new RenameDetection(new DetectedRename(compilation.AssemblyName ?? string.Empty, plan, match.Component.ToDisplayString()), null);
        }

        /// <summary>
        /// Components declared in this assembly's own code that the stale generated trees
        /// mention by type, each with the stale files that mention it. A name filter first —
        /// the semantic model only binds identifiers spelled like a component.
        /// </summary>
        static Dictionary<INamedTypeSymbol, List<string>> ReferencedComponents(
            Compilation compilation,
            GenerationInput input,
            IReadOnlyList<SyntaxTree> staleTrees)
        {
            var componentNames = new HashSet<string>(
                input.Data.OfType<ComponentData>().Select(data => data.GetTypeName().TypeName()),
                StringComparer.Ordinal);

            var found = new Dictionary<INamedTypeSymbol, List<string>>(SymbolEqualityComparer.Default);
            foreach (var tree in staleTrees)
            {
                SemanticModel? model = null;
                foreach (var name in tree.GetRoot().DescendantNodes().OfType<IdentifierNameSyntax>())
                {
                    if (!componentNames.Contains(name.Identifier.ValueText))
                        continue;

                    model ??= compilation.GetSemanticModel(tree);
                    if (model.GetSymbolInfo(name).Symbol is not INamedTypeSymbol type ||
                        !SymbolEqualityComparer.Default.Equals(type.ContainingAssembly, compilation.Assembly) ||
                        !type.DeclaringSyntaxReferences.Any(reference => !GeneratedFiles.IsGenerated(reference.SyntaxTree)))
                        continue;

                    if (!found.TryGetValue(type, out var files))
                        found[type] = files = new List<string>();

                    var path = Path.GetFullPath(tree.FilePath);
                    if (!files.Contains(path))
                        files.Add(path);
                }
            }

            return found;
        }

        /// <summary>
        /// What the component may have been called, read off the file names: every
        /// per-component file name embeds the component name, so a file about to be written
        /// for the new name and a file about to be deleted that references the component
        /// differ exactly where the name sits (Game/Components/GameHpComponent.cs against
        /// Game/Components/GameHealthComponent.cs). Only a guess — <see cref="Detect"/>
        /// verifies it by generation.
        /// </summary>
        static IEnumerable<string> OldClassNames(
            INamedTypeSymbol component,
            IReadOnlyList<string> staleFiles,
            IReadOnlyList<string> addedFiles,
            bool ignoreNamespaces)
        {
            var newBare = component.Name.RemoveComponentSuffix();
            var newFlat = component.ToCompilableString().ToComponentName(ignoreNamespaces);
            if (newBare.Length == 0 || !newFlat.EndsWith(newBare, StringComparison.Ordinal))
                yield break;

            var prefix = newFlat.Substring(0, newFlat.Length - newBare.Length);
            var comparison = OperatingSystem.IsLinux() ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;
            var seen = new HashSet<string>(StringComparer.Ordinal);

            foreach (var added in addedFiles)
            {
                for (var index = added.IndexOf(newFlat, StringComparison.Ordinal);
                     index >= 0;
                     index = added.IndexOf(newFlat, index + 1, StringComparison.Ordinal))
                {
                    var before = added.Substring(0, index);
                    var after = added.Substring(index + newFlat.Length);

                    foreach (var stale in staleFiles)
                    {
                        if (stale.Length <= before.Length + after.Length ||
                            !stale.StartsWith(before, comparison) ||
                            !stale.EndsWith(after, comparison))
                            continue;

                        var oldFlat = stale.Substring(before.Length, stale.Length - before.Length - after.Length);
                        if (!oldFlat.StartsWith(prefix, StringComparison.Ordinal))
                            continue;

                        var oldBare = oldFlat.Substring(prefix.Length);
                        if (oldBare.Length == 0 || oldBare == newBare || !SyntaxFacts.IsValidIdentifier(oldBare))
                            continue;

                        var oldClassName = component.Name.EndsWith("Component", StringComparison.Ordinal)
                            ? oldBare.AddComponentSuffix()
                            : oldBare;
                        if (seen.Add(oldClassName))
                            yield return oldClassName;
                    }
                }
            }
        }

        /// <summary>
        /// Generation as if the component were still called <paramref name="className"/>: the
        /// discovery snapshots of the compilation, with the component's name (and a
        /// [ComponentName] spelling it) swapped back. Null if that generation fails.
        /// </summary>
        static IReadOnlyList<GeneratedSource>? GenerateAsIfNamed(
            Compilation compilation,
            GenerationConfig config,
            INamedTypeSymbol component,
            string className)
        {
            var snapshots = EntitasDiscovery.GetRelevantTypes(compilation, parallel: true)
                .AsParallel()
                .Select(type => SymbolEqualityComparer.Default.Equals(type, component)
                    ? Renamed(TypeSnapshot.From(type), component.Name, className)
                    : TypeSnapshot.From(type))
                .ToArray();

            var result = EntitasGenerators.Generate(GenerationInput.From(snapshots, config));
            return result.Diagnostics.Any(d => d.Severity == DiagnosticSeverity.Error) ? null : result.Sources;
        }

        static TypeSnapshot Renamed(TypeSnapshot type, string className, string asClassName)
        {
            var bare = className.RemoveComponentSuffix();
            var asBare = asClassName.RemoveComponentSuffix();
            var fullName = type.FullName.EndsWith(className, StringComparison.Ordinal)
                ? type.FullName.Substring(0, type.FullName.Length - className.Length) + asClassName
                : type.FullName;

            var attributes = type.Attributes
                .Select(attribute => attribute.SimpleName != "ComponentNameAttribute"
                    ? attribute
                    : new AttributeSnapshot(
                        attribute.TypeNames,
                        attribute.SimpleName,
                        attribute.HasBaseType,
                        attribute.Arguments
                            .Select(argument => argument == null || argument.RemoveComponentSuffix() != bare
                                ? argument
                                : argument.EndsWith("Component", StringComparison.Ordinal) ? asBare.AddComponentSuffix() : asBare)
                            .ToArray(),
                        attribute.ContextLiteral))
                .ToArray();

            return new TypeSnapshot(fullName, type.IsAbstract, type.IsGenericType, type.IsComponent, attributes, type.Members, type.Methods);
        }

        // -- applying -------------------------------------------------------------------

        /// <summary>
        /// Writes the planned edits — all or nothing: every file is checked against the plan
        /// before the first one is written. Keeps each file's byte-order mark.
        /// </summary>
        public static List<string> Apply(DetectedRename rename)
        {
            var updates = new List<(string Path, string Text, bool Bom)>();

            // One file per path, even if two assemblies compile it: two edit sets applied to
            // the same original would each undo the other.
            var files = rename.Files
                .GroupBy(file => Path.GetFullPath(file.Path), GeneratedFileWriter.PathComparer)
                .Select(group => new FileEdits(
                    group.Key,
                    group.SelectMany(file => file.Edits)
                        .GroupBy(edit => edit.Span)
                        .Select(same => same.First())
                        .OrderBy(edit => edit.Span.Start)
                        .ToArray()));

            foreach (var file in files)
            {
                var bytes = File.ReadAllBytes(file.Path);
                var bom = bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF;
                var original = File.ReadAllText(file.Path);
                var updated = RenameEngine.Apply(original, file);
                if (updated != original)
                    updates.Add((file.Path, updated, bom));
            }

            foreach (var (path, text, bom) in updates)
                File.WriteAllText(path, text, new UTF8Encoding(bom));

            return updates.Select(update => Path.GetFullPath(update.Path)).ToList();
        }
    }

    public sealed class RenameDetection
    {
        public static readonly RenameDetection None = new RenameDetection(null, null);

        /// <summary>A rename proven by generation, with the usages left to update.</summary>
        public DetectedRename? Rename { get; }

        /// <summary>Looked like a rename, but could not be pinned down — nothing is rewritten.</summary>
        public string? Warning { get; }

        public RenameDetection(DetectedRename? rename, string? warning)
        {
            Rename = rename;
            Warning = warning;
        }
    }

    public enum RenameOutcome
    {
        /// <summary>Found, waiting for a decision; nothing was written.</summary>
        Pending,

        /// <summary>The usages were updated and the code regenerated.</summary>
        Applied,

        /// <summary>Regenerated without updating the usages, as asked.</summary>
        Ignored,

        /// <summary>Nothing used the old generated API; regenerated.</summary>
        NothingToUpdate
    }

    /// <summary>What to do when the generated files reveal a component renamed in the IDE.</summary>
    public enum RenameHandling
    {
        /// <summary>Report it and write nothing, so the old names are still there to resolve once someone decides.</summary>
        Ask,

        /// <summary>Update the usages of the old generated API, then regenerate.</summary>
        Apply,

        /// <summary>Regenerate without updating them (they stop compiling).</summary>
        Ignore
    }

    public sealed class DetectedRename
    {
        public string AssemblyName { get; }
        public RenamePlan Plan { get; }
        public string NewFullTypeName { get; }
        public string OldClassName => Plan.OldClassName;
        public string NewClassName => Plan.NewClassName;

        /// <summary>Usages in assemblies that reference this one (an Editor assembly, tests).</summary>
        public List<ExternalEdits> External { get; } = new List<ExternalEdits>();

        public RenameOutcome Outcome { get; set; } = RenameOutcome.Pending;

        /// <summary>Files rewritten, once applied.</summary>
        public List<string> EditedFiles { get; } = new List<string>();

        /// <summary>Identifies this rename in a decision (<see cref="WriterOptions.ApplyRenames"/>).</summary>
        public string Key => $"{AssemblyName}:{Plan.OldFullTypeName}>{NewFullTypeName}";

        public DetectedRename(string assemblyName, RenamePlan plan, string newFullTypeName)
        {
            AssemblyName = assemblyName;
            Plan = plan;
            NewFullTypeName = newFullTypeName;
        }

        public IEnumerable<FileEdits> Files => Plan.Files.Concat(External.SelectMany(e => e.Files));

        public int UsageCount => Plan.EditCount + External.Sum(e => e.EditCount);

        public IEnumerable<string> Warnings => Plan.Warnings.Concat(External.SelectMany(e => e.Warnings));

        /// <summary>The old identifiers in use, most used first, with their new names.</summary>
        public IReadOnlyList<(string Old, string New)> UsedIdentifiers =>
            Files.SelectMany(file => file.Edits)
                .GroupBy(edit => (edit.OldText, edit.NewText))
                .OrderByDescending(group => group.Count())
                .ThenBy(group => group.Key.OldText, StringComparer.Ordinal)
                .Select(group => group.Key)
                .ToList();
    }
}
