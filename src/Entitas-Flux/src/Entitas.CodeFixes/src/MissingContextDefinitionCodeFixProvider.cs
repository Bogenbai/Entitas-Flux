using System.Collections.Immutable;
using System.Composition;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Entitas.SourceGenerator.Analyzers;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CodeActions;
using Microsoft.CodeAnalysis.CodeFixes;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Formatting;

namespace Entitas.CodeFixes
{
    /// <summary>
    /// Fixes ENT0002 by declaring a context for the assembly. One attribute unblocks
    /// every component in it, which is why there is no Fix All: applying the fix once
    /// makes all the other diagnostics disappear.
    ///
    /// Deliberately does NOT fix ENT0003 (components in a different assembly than their
    /// contexts): declaring a context there would generate a second, parallel set of
    /// contexts and entities rather than fix anything. That one needs a human to move
    /// the file.
    /// </summary>
    [ExportCodeFixProvider(LanguageNames.CSharp, Name = nameof(MissingContextDefinitionCodeFixProvider)), Shared]
    public sealed class MissingContextDefinitionCodeFixProvider : CodeFixProvider
    {
        const string DefaultContextName = "Game";

        public override ImmutableArray<string> FixableDiagnosticIds =>
            ImmutableArray.Create(MissingContextDefinitionAnalyzer.DiagnosticId);

        public override FixAllProvider? GetFixAllProvider() => null;

        public override Task RegisterCodeFixesAsync(CodeFixContext context)
        {
            context.RegisterCodeFix(
                CodeAction.Create(
                    $"Declare the \"{DefaultContextName}\" context for this assembly",
                    cancellationToken => DeclareContextAsync(context.Document, cancellationToken),
                    equivalenceKey: nameof(MissingContextDefinitionCodeFixProvider)),
                context.Diagnostics.First());

            return Task.CompletedTask;
        }

        static async Task<Document> DeclareContextAsync(Document document, CancellationToken cancellationToken)
        {
            if (await document.GetSyntaxRootAsync(cancellationToken).ConfigureAwait(false) is not CompilationUnitSyntax root)
                return document;

            // Fully qualified on purpose: the fix must work whether or not the file has
            // `using Entitas.CodeGeneration.Attributes;`.
            var attribute = SyntaxFactory.Attribute(
                SyntaxFactory.ParseName("Entitas.CodeGeneration.Attributes.ContextDefinition"),
                SyntaxFactory.AttributeArgumentList(
                    SyntaxFactory.SingletonSeparatedList(
                        SyntaxFactory.AttributeArgument(
                            SyntaxFactory.LiteralExpression(
                                SyntaxKind.StringLiteralExpression,
                                SyntaxFactory.Literal(DefaultContextName))))));

            var attributeList = SyntaxFactory.AttributeList(
                    SyntaxFactory.AttributeTargetSpecifier(SyntaxFactory.Token(SyntaxKind.AssemblyKeyword)),
                    SyntaxFactory.SingletonSeparatedList(attribute))
                .WithAdditionalAnnotations(Formatter.Annotation);

            return document.WithSyntaxRoot(root.AddAttributeLists(attributeList));
        }
    }
}
