using System;
using System.Collections.Generic;
using System.Linq;
using Entitas.SourceGenerator.CodeGeneration;
using Entitas.SourceGenerator.Discovery;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;

namespace Entitas.SourceGenerator.Rename
{
    /// <summary>
    /// Plans the rename of an Entitas component across a whole compilation.
    ///
    /// The identifier map is not hand-written: the engine runs the real source
    /// generator twice — once on the compilation as-is, once on a copy where only the
    /// component's declaration is renamed — and diffs the DECLARED names in both
    /// outputs. Whatever the generator stops declaring, paired with whatever it starts
    /// declaring, IS the rename map. That keeps the tool correct by construction when
    /// generators are added or their naming changes.
    ///
    /// Usages are then resolved through a semantic model of the compilation plus the
    /// generated trees, so only members that really come from generated Entitas code
    /// (or the component type itself) are rewritten — an unrelated `foo.Health` on some
    /// other type is left alone.
    /// </summary>
    public static class RenameEngine
    {
        /// <summary>Virtual path prefix for the in-memory generated trees.</summary>
        const string GeneratedPrefix = "<entitas-generated>/";

        public static RenamePlan CreatePlan(
            Compilation compilation,
            string oldName,
            string newName,
            RenameOptions? options = null)
        {
            options ??= new RenameOptions();

            var oldBare = oldName.RemoveComponentSuffix();
            var newBare = newName.RemoveComponentSuffix();
            if (oldBare.Length == 0 || newBare.Length == 0)
                throw new RenameException("Component names must not be empty.");
            if (oldBare == newBare)
                throw new RenameException($"'{oldName}' and '{newName}' name the same component.");

            var target = FindTarget(compilation, oldName, oldBare);
            EnsureNameIsFree(compilation, target, newBare);
            var oldClassName = target.Name;
            var newClassName = oldClassName.EndsWith("Component", StringComparison.Ordinal)
                ? newBare.AddComponentSuffix()
                : newBare;

            var generated = EntitasGenerators.Generate(GenerationInput.From(compilation));
            var failures = generated.Diagnostics
                .Where(d => d.Severity == DiagnosticSeverity.Error)
                .Select(d => d.GetMessage())
                .ToArray();
            if (failures.Length > 0)
                throw new RenameException(
                    "code generation failed, so the set of identifiers to rename cannot be trusted:\n  " +
                    string.Join("\n  ", failures));

            var oldSources = generated.Sources;
            if (oldSources.Count == 0)
                throw new RenameException(
                    "the engine produced no output. The assembly must reference Entitas " +
                    "(Entitas.IComponent) AND declare at least one [assembly: ContextDefinition(\"...\")].");

            var renamedCompilation = WithRenamedDeclaration(compilation, target, newClassName, oldBare, newBare);
            var newSources = EntitasGenerators.Generate(renamedCompilation);

            var warnings = new List<string>();
            var substitutions = Substitutions(target, oldClassName, newClassName, oldBare, newBare);
            var nameMap = BuildNameMap(
                DeclaredNames(oldSources),
                DeclaredNames(newSources),
                substitutions,
                warnings);

            // The component class itself is declared in user code, so it never shows up
            // in the generated diff — it is always part of the rename.
            nameMap[oldClassName] = newClassName;

            var files = CollectEdits(compilation, target, oldSources, nameMap, substitutions, options, warnings);

            var declarationFiles = target.DeclaringSyntaxReferences
                .Select(r => r.SyntaxTree.FilePath)
                .Where(p => !string.IsNullOrEmpty(p))
                .Distinct()
                .ToArray();

            return new RenamePlan
            {
                OldFullTypeName = target.ToDisplayString(),
                OldClassName = oldClassName,
                NewClassName = newClassName,
                ContextNames = ContextNamesOf(compilation, target),
                DeclarationFile = declarationFiles.Length == 1 ? declarationFiles[0] : null,
                NameMap = nameMap,
                Files = files,
                Warnings = warnings,
                DeclaringAssemblyName = compilation.AssemblyName ?? string.Empty,
                GeneratedTypeNames = DeclaredTypeNames(oldSources)
            };
        }

        /// <summary>Applies a planned edit set to a file's text.</summary>
        public static string Apply(string originalText, FileEdits file)
        {
            foreach (var edit in file.Edits)
            {
                if (edit.Span.End > originalText.Length ||
                    originalText.Substring(edit.Span.Start, edit.Span.Length) != edit.OldText)
                {
                    throw new RenameException(
                        $"{file.Path} changed since the plan was made (expected '{edit.OldText}' " +
                        $"at offset {edit.Span.Start}) — re-run the tool.");
                }
            }

            var text = SourceText.From(originalText);
            var changes = file.Edits
                .OrderBy(e => e.Span.Start)
                .Select(e => new TextChange(e.Span, e.NewText));

            return text.WithChanges(changes).ToString();
        }

        // -- target lookup ----------------------------------------------------

        static INamedTypeSymbol FindTarget(Compilation compilation, string oldName, string oldBare)
        {
            var candidates = Components(compilation);

            // Exact full-name match wins over a bare-name match, so an ambiguous short
            // name can always be disambiguated by passing the namespace.
            var exact = candidates
                .Where(t => t.ToDisplayString() == oldName || t.ToDisplayString() == oldName.AddComponentSuffix())
                .ToArray();
            if (exact.Length == 1)
                return exact[0];

            var matches = candidates
                .Where(t => t.Name.RemoveComponentSuffix() == oldBare)
                .ToArray();

            if (matches.Length == 1)
                return matches[0];

            if (matches.Length == 0)
            {
                var similar = candidates
                    .Where(t => t.Name.IndexOf(oldBare, StringComparison.OrdinalIgnoreCase) >= 0)
                    .Select(t => t.ToDisplayString())
                    .OrderBy(n => n, StringComparer.Ordinal)
                    .Take(5)
                    .ToArray();

                throw new RenameException(
                    $"no component named '{oldName}' found in this compilation. " +
                    "Pass the class name (e.g. Health or HealthComponent) of a type implementing Entitas.IComponent." +
                    (similar.Length > 0 ? "\nDid you mean:\n  " + string.Join("\n  ", similar) : string.Empty));
            }

            throw new RenameException(
                $"'{oldName}' is ambiguous — pass the full name instead:\n  " +
                string.Join("\n  ", matches.Select(m => m.ToDisplayString())));
        }

        /// <summary>
        /// Refuses to merge two components: renaming onto an existing name in the same
        /// namespace would make the generator emit conflicting members.
        /// </summary>
        static void EnsureNameIsFree(Compilation compilation, INamedTypeSymbol target, string newBare)
        {
            var clash = Components(compilation).FirstOrDefault(t =>
                t.Name.RemoveComponentSuffix() == newBare &&
                t.ContainingNamespace.ToDisplayString() == target.ContainingNamespace.ToDisplayString());

            if (clash != null)
                throw new RenameException(
                    $"'{clash.ToDisplayString()}' already exists — pick a name that is still free.");
        }

        static INamedTypeSymbol[] Components(Compilation compilation)
        {
            var componentInterface = compilation.GetTypeByMetadataName(WellKnownTypes.ComponentInterface);
            if (componentInterface is null)
                throw new RenameException(
                    "this compilation does not reference Entitas (Entitas.IComponent could not be resolved).");

            return EntitasDiscovery.GetCandidateTypes(compilation)
                .Where(t => !t.IsAbstract)
                .Where(t => t.AllInterfaces.Any(i =>
                    SymbolEqualityComparer.Default.Equals(i, componentInterface)))
                .ToArray();
        }

        static string[] ContextNamesOf(Compilation compilation, INamedTypeSymbol target)
        {
            try
            {
                return ContextResolver.FromCompilation(compilation).GetContextNames(TypeSnapshot.From(target));
            }
            catch
            {
                return new string[0];
            }
        }

        // -- regeneration under the new name ----------------------------------

        /// <summary>
        /// Returns a copy of the compilation with the component's declaration renamed —
        /// the class identifier plus any [ComponentName("Old")] argument naming it, since
        /// that attribute (not the class name) drives generation when present.
        /// </summary>
        static Compilation WithRenamedDeclaration(
            Compilation compilation,
            INamedTypeSymbol target,
            string newClassName,
            string oldBare,
            string newBare)
        {
            foreach (var group in target.DeclaringSyntaxReferences.GroupBy(r => r.SyntaxTree))
            {
                var tree = group.Key;
                var changes = new List<TextChange>();

                foreach (var reference in group)
                {
                    if (reference.GetSyntax() is not BaseTypeDeclarationSyntax declaration)
                        continue;

                    changes.Add(new TextChange(declaration.Identifier.Span, newClassName));

                    foreach (var argument in ComponentNameArguments(declaration))
                    {
                        var value = argument.Token.ValueText;
                        if (value.RemoveComponentSuffix() != oldBare)
                            continue;

                        var renamed = value.EndsWith("Component", StringComparison.Ordinal)
                            ? newBare.AddComponentSuffix()
                            : newBare;
                        changes.Add(new TextChange(argument.Span, $"\"{renamed}\""));
                    }
                }

                if (changes.Count == 0)
                    continue;

                var text = tree.GetText().WithChanges(changes.OrderBy(c => c.Span.Start));
                compilation = compilation.ReplaceSyntaxTree(tree, tree.WithChangedText(text));
            }

            return compilation;
        }

        static IEnumerable<LiteralExpressionSyntax> ComponentNameArguments(BaseTypeDeclarationSyntax declaration) =>
            declaration.AttributeLists
                .SelectMany(list => list.Attributes)
                .Where(attribute => attribute.Name.ToString().TrimEnd().EndsWith("ComponentName", StringComparison.Ordinal)
                                    || attribute.Name.ToString().TrimEnd().EndsWith("ComponentNameAttribute", StringComparison.Ordinal))
                .SelectMany(attribute => attribute.ArgumentList?.Arguments ?? default)
                .Select(argument => argument.Expression)
                .OfType<LiteralExpressionSyntax>()
                .Where(literal => literal.IsKind(SyntaxKind.StringLiteralExpression));

        // -- name map ---------------------------------------------------------

        /// <summary>
        /// Substring rewrites that turn any old derived identifier into its new
        /// counterpart, longest first: the flattened component name (what generated
        /// members are built from), the short class name (used by e.g. {X}Changed) and
        /// the class name itself, each in both casings.
        /// </summary>
        static (string From, string To)[] Substitutions(
            INamedTypeSymbol target,
            string oldClassName,
            string newClassName,
            string oldBare,
            string newBare)
        {
            var oldFlat = target.ToDisplayString().ToComponentName(false);
            var newFlat = oldFlat.Length > oldBare.Length
                ? oldFlat.Substring(0, oldFlat.Length - oldBare.Length) + newBare
                : newBare;

            var pairs = new List<(string, string)>
            {
                (oldClassName, newClassName),
                (oldFlat, newFlat),
                (oldFlat.ToLowerFirst(), newFlat.ToLowerFirst()),
                (oldBare, newBare),
                (oldBare.ToLowerFirst(), newBare.ToLowerFirst())
            };

            return pairs
                .Where(p => p.Item1.Length > 0)
                .Distinct()
                .OrderByDescending(p => p.Item1.Length)
                .ToArray();
        }

        static Dictionary<string, string> BuildNameMap(
            HashSet<string> oldDeclarations,
            HashSet<string> newDeclarations,
            (string From, string To)[] substitutions,
            List<string> warnings)
        {
            var map = new Dictionary<string, string>(StringComparer.Ordinal);
            var added = new HashSet<string>(
                newDeclarations.Where(n => !oldDeclarations.Contains(n)), StringComparer.Ordinal);

            foreach (var name in oldDeclarations.Where(n => !newDeclarations.Contains(n)).OrderBy(n => n, StringComparer.Ordinal))
            {
                string? confirmed = null;
                string? guess = null;

                foreach (var (from, to) in substitutions)
                {
                    if (!name.Contains(from))
                        continue;

                    var candidate = name.Replace(from, to);
                    if (candidate == name)
                        continue;

                    guess ??= candidate;
                    if (!added.Contains(candidate))
                        continue;

                    confirmed = candidate;
                    break;
                }

                if (confirmed != null)
                {
                    map[name] = confirmed;
                }
                else if (guess != null)
                {
                    map[name] = guess;
                    warnings.Add(
                        $"'{name}' -> '{guess}': the regenerated output declares no such member, " +
                        "so this pair is a guess — check the result.");
                }
                else
                {
                    warnings.Add(
                        $"generated member '{name}' disappears after the rename but could not be mapped " +
                        "to a new name — fix its usages by hand.");
                }
            }

            return map;
        }

        /// <summary>Type names the generator declares — the anchor for recognising this API elsewhere.</summary>
        static HashSet<string> DeclaredTypeNames(IReadOnlyList<GeneratedSource> sources)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);

            foreach (var source in sources)
            {
                var root = CSharpSyntaxTree.ParseText(source.Content).GetRoot();
                foreach (var node in root.DescendantNodes())
                {
                    switch (node)
                    {
                        case BaseTypeDeclarationSyntax type:
                            names.Add(type.Identifier.ValueText);
                            break;
                        case DelegateDeclarationSyntax @delegate:
                            names.Add(@delegate.Identifier.ValueText);
                            break;
                    }
                }
            }

            return names;
        }

        static HashSet<string> DeclaredNames(IReadOnlyList<GeneratedSource> sources)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);

            foreach (var source in sources)
            {
                var root = CSharpSyntaxTree.ParseText(source.Content).GetRoot();
                foreach (var node in root.DescendantNodes())
                {
                    switch (node)
                    {
                        case BaseTypeDeclarationSyntax type:
                            names.Add(type.Identifier.ValueText);
                            break;
                        case DelegateDeclarationSyntax @delegate:
                            names.Add(@delegate.Identifier.ValueText);
                            break;
                        case MethodDeclarationSyntax method:
                            names.Add(method.Identifier.ValueText);
                            break;
                        case PropertyDeclarationSyntax property:
                            names.Add(property.Identifier.ValueText);
                            break;
                        case EventDeclarationSyntax @event:
                            names.Add(@event.Identifier.ValueText);
                            break;
                        case EnumMemberDeclarationSyntax enumMember:
                            names.Add(enumMember.Identifier.ValueText);
                            break;
                        case VariableDeclaratorSyntax variable
                            when variable.Parent?.Parent is BaseFieldDeclarationSyntax:
                            names.Add(variable.Identifier.ValueText);
                            break;
                    }
                }
            }

            return names;
        }

        // -- usage rewriting --------------------------------------------------

        static IReadOnlyList<FileEdits> CollectEdits(
            Compilation compilation,
            INamedTypeSymbol target,
            IReadOnlyList<GeneratedSource> generatedSources,
            IReadOnlyDictionary<string, string> nameMap,
            (string From, string To)[] substitutions,
            RenameOptions options,
            List<string> warnings)
        {
            // In file mode the generated code is already in the compilation, as real files:
            // resolve against those (adding a second copy would make every generated
            // member ambiguous) and rewrite them too, so the project compiles straight
            // after the rename — regeneration then only moves them to their new names.
            var onDisk = GeneratedFiles.FindGeneratedTrees(compilation);
            Compilation semanticCompilation;
            HashSet<string> generatedPaths;
            if (onDisk.Count > 0)
            {
                semanticCompilation = compilation;
                generatedPaths = new HashSet<string>(onDisk.Select(t => t.FilePath), StringComparer.Ordinal);
            }
            else
            {
                var generatedTrees = InMemoryTrees(compilation, generatedSources);
                semanticCompilation = compilation.AddSyntaxTrees(generatedTrees);
                generatedPaths = new HashSet<string>(generatedTrees.Select(t => t.FilePath), StringComparer.Ordinal);
            }

            // Names that are plain enough to belong to anything (the component name
            // itself, e.g. `Health` / `health`); these are only rewritten when the
            // semantic model confirms where they come from.
            var ambiguous = new HashSet<string>(
                substitutions.Where(s => s.From != target.Name).Select(s => s.From),
                StringComparer.Ordinal);

            // Symbols resolved through `semanticCompilation` are different instances
            // from `target` (a different Compilation), so identity is compared by
            // fully-qualified name rather than by SymbolEqualityComparer.
            var targetDisplay = target.ToDisplayString();

            var files = new List<FileEdits>();

            foreach (var tree in compilation.SyntaxTrees)
            {
                var text = tree.GetText().ToString();
                if (!nameMap.Keys.Any(name => text.IndexOf(name, StringComparison.Ordinal) >= 0))
                    continue;

                var model = semanticCompilation.GetSemanticModel(tree);
                var root = tree.GetRoot();
                var edits = new List<TextEdit>();

                // A generated file on disk also DECLARES the derived members (AddHealth,
                // hasHealth, GameMatcher.Health, …). Every name in the map is, by
                // construction, one the generator stops declaring for this component, so in
                // our own files each such declaration is renamed along with its usages.
                var isGeneratedFile = onDisk.Count > 0 && generatedPaths.Contains(tree.FilePath);

                foreach (var node in root.DescendantNodes())
                {
                    if (isGeneratedFile && DeclaredIdentifier(node) is { } declared &&
                        nameMap.TryGetValue(declared.ValueText, out var renamedDeclaration))
                    {
                        edits.Add(Edit(tree, declared, renamedDeclaration));
                        continue;
                    }

                    switch (node)
                    {
                        case SimpleNameSyntax name when nameMap.ContainsKey(name.Identifier.ValueText):
                            TryRewriteUsage(name, model, targetDisplay, generatedPaths, nameMap, ambiguous, tree, edits, warnings);
                            break;

                        // The component's own declaration (and its constructors).
                        case BaseTypeDeclarationSyntax type when type.Identifier.ValueText == target.Name:
                            if (model.GetDeclaredSymbol(type)?.ToDisplayString() == targetDisplay)
                                edits.Add(Edit(tree, type.Identifier, nameMap[target.Name]));
                            break;

                        case ConstructorDeclarationSyntax constructor
                            when constructor.Identifier.ValueText == target.Name:
                            edits.Add(Edit(tree, constructor.Identifier, nameMap[target.Name]));
                            break;

                        // A user method implementing a generated interface member — the
                        // OnAnyHealth of an IAnyHealthListener — follows the member's new name.
                        case MethodDeclarationSyntax method when nameMap.ContainsKey(method.Identifier.ValueText):
                            if (model.GetDeclaredSymbol(method) is { } declaredMethod &&
                                ImplementedInterfaceMembers(declaredMethod)
                                    .Any(member => IsEntitasGenerated(member, targetDisplay, generatedPaths)))
                                edits.Add(Edit(tree, method.Identifier, nameMap[method.Identifier.ValueText]));
                            break;
                    }
                }

                if (options.IncludeStrings)
                    CollectTextEdits(tree, root, substitutions, edits);

                if (edits.Count > 0)
                {
                    files.Add(new FileEdits(
                        tree.FilePath,
                        edits.OrderBy(e => e.Span.Start).ToArray()));
                }
            }

            return files;
        }

        /// <summary>The interface members a method implements, implicitly or explicitly.</summary>
        internal static IEnumerable<ISymbol> ImplementedInterfaceMembers(IMethodSymbol method)
        {
            foreach (var member in method.ExplicitInterfaceImplementations)
                yield return member;

            var type = method.ContainingType;
            if (type == null)
                yield break;

            foreach (var @interface in type.AllInterfaces)
            {
                foreach (var member in @interface.GetMembers(method.Name))
                {
                    if (SymbolEqualityComparer.Default.Equals(type.FindImplementationForInterfaceMember(member), method))
                        yield return member;
                }
            }
        }

        static SyntaxToken? DeclaredIdentifier(SyntaxNode node) => node switch
        {
            BaseTypeDeclarationSyntax type => type.Identifier,
            DelegateDeclarationSyntax @delegate => @delegate.Identifier,
            MethodDeclarationSyntax method => method.Identifier,
            ConstructorDeclarationSyntax constructor => constructor.Identifier,
            PropertyDeclarationSyntax property => property.Identifier,
            EventDeclarationSyntax @event => @event.Identifier,
            EnumMemberDeclarationSyntax member => member.Identifier,
            VariableDeclaratorSyntax { Parent.Parent: BaseFieldDeclarationSyntax } variable => variable.Identifier,
            _ => null
        };

        static SyntaxTree[] InMemoryTrees(Compilation compilation, IReadOnlyList<GeneratedSource> sources)
        {
            var parseOptions = compilation.SyntaxTrees
                .Select(t => t.Options)
                .OfType<CSharpParseOptions>()
                .FirstOrDefault();

            return sources
                .Select(s => CSharpSyntaxTree.ParseText(s.Content, parseOptions, path: GeneratedPrefix + s.FileName))
                .ToArray();
        }

        /// <summary>
        /// The compilation with the generated API in it, as the compiler would see it: as
        /// is when the generated files are already part of it (file mode), otherwise with
        /// the source generator's output added. What a referencing assembly must be
        /// compiled against to resolve generated members.
        /// </summary>
        public static Compilation WithGeneratedCode(Compilation compilation)
        {
            if (GeneratedFiles.FindGeneratedTrees(compilation).Count > 0)
                return compilation;

            return compilation.AddSyntaxTrees(InMemoryTrees(compilation, EntitasGenerators.Generate(compilation)));
        }

        static void TryRewriteUsage(
            SimpleNameSyntax name,
            SemanticModel model,
            string targetDisplay,
            HashSet<string> generatedPaths,
            IReadOnlyDictionary<string, string> nameMap,
            HashSet<string> ambiguous,
            SyntaxTree tree,
            List<TextEdit> edits,
            List<string> warnings)
        {
            var identifier = name.Identifier.ValueText;
            var replacement = nameMap[identifier];

            var info = model.GetSymbolInfo(name);
            var symbols = info.Symbol != null
                ? new[] { info.Symbol }
                : info.CandidateSymbols.ToArray();

            if (symbols.Length > 0)
            {
                if (symbols.Any(symbol => IsEntitasGenerated(symbol, targetDisplay, generatedPaths)))
                    edits.Add(Edit(tree, name.Identifier, replacement));

                return;
            }

            // Unresolved (the compilation may be missing a reference). Distinctive
            // names like AddHealth/hasHealth can only be ours, so still rewrite them;
            // a bare `Health` is left for a human.
            if (!ambiguous.Contains(identifier))
            {
                edits.Add(Edit(tree, name.Identifier, replacement));
                return;
            }

            warnings.Add(
                $"{tree.FilePath}:{Line(tree, name.Identifier.Span)}: '{identifier}' could not be resolved " +
                "and is too generic to rename blindly — left unchanged.");
        }

        static bool IsEntitasGenerated(ISymbol symbol, string targetDisplay, HashSet<string> generatedPaths)
        {
            var definition = symbol.OriginalDefinition;

            if (definition is INamedTypeSymbol type && type.ToDisplayString() == targetDisplay)
                return true;

            if (definition is IMethodSymbol { MethodKind: MethodKind.Constructor } constructor &&
                constructor.ContainingType.ToDisplayString() == targetDisplay)
                return true;

            return definition.Locations.Any(location =>
                location.IsInSource &&
                location.SourceTree != null &&
                generatedPaths.Contains(location.SourceTree.FilePath));
        }

        /// <summary>Occurrences inside string literals and comments (--include-strings).</summary>
        static void CollectTextEdits(
            SyntaxTree tree,
            SyntaxNode root,
            (string From, string To)[] substitutions,
            List<TextEdit> edits)
        {
            foreach (var token in root.DescendantTokens())
            {
                if (token.IsKind(SyntaxKind.StringLiteralToken))
                    AddTextEdits(tree, token.Text, token.SpanStart, substitutions, edits);

                foreach (var trivia in token.LeadingTrivia.Concat(token.TrailingTrivia))
                {
                    if (trivia.IsKind(SyntaxKind.SingleLineCommentTrivia) ||
                        trivia.IsKind(SyntaxKind.MultiLineCommentTrivia) ||
                        trivia.IsKind(SyntaxKind.SingleLineDocumentationCommentTrivia))
                        AddTextEdits(tree, trivia.ToString(), trivia.SpanStart, substitutions, edits);
                }
            }
        }

        static void AddTextEdits(
            SyntaxTree tree,
            string content,
            int offset,
            (string From, string To)[] substitutions,
            List<TextEdit> edits)
        {
            var taken = new List<TextSpan>();

            foreach (var (from, to) in substitutions)
            {
                var index = content.IndexOf(from, StringComparison.Ordinal);
                while (index >= 0)
                {
                    var span = new TextSpan(offset + index, from.Length);
                    if (!taken.Any(t => t.OverlapsWith(span)))
                    {
                        taken.Add(span);
                        edits.Add(new TextEdit(span, from, to, Line(tree, span)));
                    }

                    index = content.IndexOf(from, index + from.Length, StringComparison.Ordinal);
                }
            }
        }

        static TextEdit Edit(SyntaxTree tree, SyntaxToken token, string replacement) =>
            new TextEdit(
                token.Span,
                token.Text,
                replacement.AddPrefixIfIsKeyword(),
                Line(tree, token.Span));

        static int Line(SyntaxTree tree, TextSpan span) =>
            tree.GetLineSpan(span).StartLinePosition.Line + 1;
    }

    public static class ExternalRename
    {
        /// <summary>
        /// Finds usages of a planned rename in an assembly that only REFERENCES the one
        /// declaring the component (an Editor assembly, a test asmdef, …). There the
        /// generated code is not visible as source, so an identifier is accepted when its
        /// symbol comes from the declaring assembly and belongs either to the component
        /// itself or to one of the types the generator declares.
        /// </summary>
        public static ExternalEdits CollectEdits(Compilation referencing, RenamePlan plan)
        {
            var warnings = new List<string>();
            var files = new List<FileEdits>();
            var generatedTypes = new HashSet<string>(plan.GeneratedTypeNames, StringComparer.Ordinal);
            var assemblyName = referencing.AssemblyName ?? string.Empty;

            if (plan.NameMap.Count == 0 || plan.DeclaringAssemblyName.Length == 0)
                return new ExternalEdits(assemblyName, files, warnings);

            foreach (var tree in referencing.SyntaxTrees)
            {
                var text = tree.GetText().ToString();
                if (!plan.NameMap.Keys.Any(name => text.IndexOf(name, StringComparison.Ordinal) >= 0))
                    continue;

                var model = referencing.GetSemanticModel(tree);
                var edits = new List<TextEdit>();

                foreach (var method in tree.GetRoot().DescendantNodes().OfType<MethodDeclarationSyntax>())
                {
                    if (!plan.NameMap.TryGetValue(method.Identifier.ValueText, out var renamedMethod))
                        continue;

                    if (model.GetDeclaredSymbol(method) is { } declared &&
                        RenameEngine.ImplementedInterfaceMembers(declared)
                            .Any(member => BelongsToRenamedApi(member, plan, generatedTypes)))
                    {
                        edits.Add(new TextEdit(
                            method.Identifier.Span,
                            method.Identifier.Text,
                            renamedMethod.AddPrefixIfIsKeyword(),
                            Line(tree, method.Identifier.Span)));
                    }
                }

                foreach (var name in tree.GetRoot().DescendantNodes().OfType<SimpleNameSyntax>())
                {
                    var identifier = name.Identifier.ValueText;
                    if (!plan.NameMap.TryGetValue(identifier, out var replacement))
                        continue;

                    var info = model.GetSymbolInfo(name);
                    var symbols = info.Symbol != null
                        ? new[] { info.Symbol }
                        : info.CandidateSymbols.ToArray();

                    if (symbols.Length == 0)
                    {
                        warnings.Add(
                            $"{tree.FilePath}:{Line(tree, name.Identifier.Span)}: '{identifier}' could not be " +
                            $"resolved in {assemblyName} — left unchanged.");
                        continue;
                    }

                    if (symbols.Any(symbol => BelongsToRenamedApi(symbol, plan, generatedTypes)))
                    {
                        edits.Add(new TextEdit(
                            name.Identifier.Span,
                            name.Identifier.Text,
                            replacement.AddPrefixIfIsKeyword(),
                            Line(tree, name.Identifier.Span)));
                    }
                }

                if (edits.Count > 0)
                    files.Add(new FileEdits(tree.FilePath, edits.OrderBy(e => e.Span.Start).ToArray()));
            }

            return new ExternalEdits(assemblyName, files, warnings);
        }

        static bool BelongsToRenamedApi(ISymbol symbol, RenamePlan plan, HashSet<string> generatedTypes)
        {
            var definition = symbol.OriginalDefinition;

            if (definition.ContainingAssembly?.Name != plan.DeclaringAssemblyName)
                return false;

            if (definition is INamedTypeSymbol type)
                return type.ToDisplayString() == plan.OldFullTypeName || generatedTypes.Contains(type.Name);

            var containingType = definition.ContainingType;
            return containingType != null &&
                   (containingType.ToDisplayString() == plan.OldFullTypeName ||
                    generatedTypes.Contains(containingType.Name));
        }

        static int Line(SyntaxTree tree, TextSpan span) =>
            tree.GetLineSpan(span).StartLinePosition.Line + 1;
    }

    public sealed class RenameException : Exception
    {
        public RenameException(string message) : base(message) { }
    }
}
