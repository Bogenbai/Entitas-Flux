using System.Collections.Generic;
using System.Collections.Immutable;
using System.Composition;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Entitas.SourceGenerator.Analyzers;
using Entitas.SourceGenerator.Rename;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CodeActions;
using Microsoft.CodeAnalysis.CodeFixes;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;

namespace Entitas.CodeFixes
{
    /// <summary>
    /// Carries out a pending [RenameTo] rename from the IDE: renames the component and
    /// every identifier the source generator derives from its name (hasX, AddX,
    /// ReplaceX, SafeRemoveX, isX, GameMatcher.X, XChanged, listeners …), then removes
    /// the attribute.
    ///
    /// The work is done by RenameEngine — the same engine entitas-rename uses — so the
    /// IDE and the CLI can never disagree about which identifiers change.
    /// </summary>
    [ExportCodeFixProvider(LanguageNames.CSharp, Name = nameof(RenameToCodeFixProvider)), Shared]
    public sealed class RenameToCodeFixProvider : CodeFixProvider
    {
        public override ImmutableArray<string> FixableDiagnosticIds =>
            ImmutableArray.Create(RenameToAnalyzer.DiagnosticId);

        /// <summary>
        /// Deliberately no Fix All: each rename is planned against the current state of
        /// the solution, so batching several of them would apply edits computed against
        /// stale text. Renames are applied one component at a time.
        /// </summary>
        public override FixAllProvider? GetFixAllProvider() => null;

        public override async Task RegisterCodeFixesAsync(CodeFixContext context)
        {
            var diagnostic = context.Diagnostics.First();

            if (!diagnostic.Properties.TryGetValue(RenameToAnalyzer.NewNameProperty, out var newName) ||
                string.IsNullOrWhiteSpace(newName))
                return;

            var root = await context.Document.GetSyntaxRootAsync(context.CancellationToken).ConfigureAwait(false);
            var declaration = root?
                .FindNode(diagnostic.Location.SourceSpan)
                .AncestorsAndSelf()
                .OfType<TypeDeclarationSyntax>()
                .FirstOrDefault();
            if (declaration == null)
                return;

            var oldTypeName = declaration.Identifier.ValueText;

            context.RegisterCodeFix(
                CodeAction.Create(
                    $"Rename component '{oldTypeName}' to '{newName}' and update all usages",
                    cancellationToken => RenameAsync(context.Document, declaration, newName!, cancellationToken),
                    equivalenceKey: nameof(RenameToCodeFixProvider)),
                diagnostic);
        }

        static async Task<Solution> RenameAsync(
            Document document,
            TypeDeclarationSyntax declaration,
            string newName,
            CancellationToken cancellationToken)
        {
            var solution = document.Project.Solution;
            var oldTypeName = declaration.Identifier.ValueText;

            // 1. Drop the attribute first, so the rename is planned against the text the
            //    edits will be applied to (spans must line up).
            var root = await document.GetSyntaxRootAsync(cancellationToken).ConfigureAwait(false);
            if (root == null)
                return solution;

            var stripped = solution.WithDocumentSyntaxRoot(
                document.Id,
                root.ReplaceNode(declaration, WithoutRenameToAttribute(declaration)));

            var project = stripped.GetProject(document.Project.Id);
            if (project == null)
                return solution;

            var compilation = await project.GetCompilationAsync(cancellationToken).ConfigureAwait(false);
            if (compilation == null)
                return solution;

            try
            {
                var plan = RenameEngine.CreatePlan(WithoutGeneratedTrees(compilation, project), oldTypeName, newName);

                // Usages in assemblies that only REFERENCE this one (Editor assemblies,
                // test asmdefs) must be collected against the solution as it is now:
                // once the declaring project is renamed, the old names no longer resolve.
                var external = await CollectExternalAsync(stripped, project.Id, plan, cancellationToken)
                    .ConfigureAwait(false);

                var renamed = await ApplyAsync(stripped, plan.Files, cancellationToken).ConfigureAwait(false);
                return await ApplyAsync(renamed, external, cancellationToken).ConfigureAwait(false);
            }
            catch (RenameException)
            {
                // The rename is impossible as requested (name taken, ambiguous, …).
                // Leave everything untouched so the diagnostic stays visible.
                return solution;
            }
        }

        /// <summary>
        /// The IDE's compilation already contains the source-generated trees; the engine
        /// adds its own copy for symbol resolution, and generating from generated code
        /// would feed synthesized components back into discovery. So the trees that do
        /// not belong to a real document are removed first.
        /// </summary>
        static Compilation WithoutGeneratedTrees(Compilation compilation, Project project)
        {
            var documentPaths = project.Documents
                .Select(d => d.FilePath)
                .Where(path => !string.IsNullOrEmpty(path))
                .ToImmutableHashSet();

            var generated = compilation.SyntaxTrees
                .Where(tree => string.IsNullOrEmpty(tree.FilePath) || !documentPaths.Contains(tree.FilePath))
                .ToArray();

            return generated.Length == 0 ? compilation : compilation.RemoveSyntaxTrees(generated);
        }

        /// <summary>
        /// Every project that reaches the declaring one through project references,
        /// directly or transitively.
        /// </summary>
        static ImmutableArray<ProjectId> ReferencingProjects(Solution solution, ProjectId declaringId)
        {
            var referencing = new HashSet<ProjectId>();

            for (var grew = true; grew;)
            {
                grew = false;
                foreach (var project in solution.Projects)
                {
                    if (project.Id == declaringId || referencing.Contains(project.Id))
                        continue;

                    if (project.ProjectReferences.Any(reference =>
                            reference.ProjectId == declaringId || referencing.Contains(reference.ProjectId)))
                    {
                        referencing.Add(project.Id);
                        grew = true;
                    }
                }
            }

            return referencing.ToImmutableArray();
        }

        static async Task<List<FileEdits>> CollectExternalAsync(
            Solution solution,
            ProjectId declaringId,
            RenamePlan plan,
            CancellationToken cancellationToken)
        {
            var files = new List<FileEdits>();

            foreach (var projectId in ReferencingProjects(solution, declaringId))
            {
                var project = solution.GetProject(projectId);
                if (project == null)
                    continue;

                var compilation = await project.GetCompilationAsync(cancellationToken).ConfigureAwait(false);
                if (compilation == null)
                    continue;

                files.AddRange(ExternalRename.CollectEdits(compilation, plan).Files);
            }

            return files;
        }

        static async Task<Solution> ApplyAsync(
            Solution solution,
            IReadOnlyList<FileEdits> plan,
            CancellationToken cancellationToken)
        {
            foreach (var file in plan)
            {
                var documentId = solution.GetDocumentIdsWithFilePath(file.Path).FirstOrDefault();
                if (documentId == null)
                    continue;

                var text = await solution.GetDocument(documentId)!
                    .GetTextAsync(cancellationToken)
                    .ConfigureAwait(false);

                solution = solution.WithDocumentText(
                    documentId,
                    SourceText.From(RenameEngine.Apply(text.ToString(), file), text.Encoding));
            }

            return solution;
        }

        static TypeDeclarationSyntax WithoutRenameToAttribute(TypeDeclarationSyntax declaration)
        {
            var lists = declaration.AttributeLists
                .Select(list => list.WithAttributes(
                    SyntaxFactory.SeparatedList(list.Attributes.Where(attribute => !IsRenameTo(attribute)))))
                .Where(list => list.Attributes.Count > 0)
                .ToArray();

            return declaration
                .WithAttributeLists(SyntaxFactory.List(lists))
                .WithTriviaFrom(declaration);
        }

        static bool IsRenameTo(AttributeSyntax attribute)
        {
            var name = attribute.Name.ToString();
            var simpleName = name.Substring(name.LastIndexOf('.') + 1);
            return simpleName == "RenameTo" || simpleName == "RenameToAttribute";
        }
    }
}
