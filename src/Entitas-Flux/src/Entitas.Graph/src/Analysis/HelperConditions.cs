using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Entitas.Graph.Analysis
{
    /// <summary>
    /// Which bool parameters decide whether a helper runs the delegate it is handed, read from the
    /// helper's own body. `entity.With(x =&gt; x.isY = true, when: c)` sets Y only when c holds —
    /// because With says `if (when) apply(self);`, not because its parameter is called `when`. So
    /// `Apply(x =&gt; …, gate: c)` is conditional on c, `Unless(x =&gt; …, skip: c)` on !c, and
    /// `Also(x =&gt; …, force: c)`, which runs the delegate either way, on nothing — whatever the
    /// parameters are called.
    ///
    /// A condition shown must hold, so this is sound before it is complete: a parameter counts
    /// only when every invocation of the delegate in the body sits under it (an if or its else,
    /// `?:`, the right side of &amp;&amp; or ||, a guard before it that returns), and nothing assigns
    /// it. A delegate that escapes — stored, handed to another call, run inside a lambda — runs
    /// who knows when, and gives no condition. Syntax only, by name: C# lets nothing in a method
    /// shadow its parameters, and binding every helper's body would cost a semantic model each.
    /// </summary>
    static class HelperConditions
    {
        /// <returns>
        /// The bool parameters every invocation of <paramref name="delegateParameter"/> depends on,
        /// each with whether it must be false for the delegate to run; empty when nothing is known.
        /// Null when the helper has no body here — it comes from a DLL, or is abstract, an
        /// interface's, or extern: then only its parameter names can say (TreeWalker's fallback).
        /// </returns>
        public static IReadOnlyList<(string Parameter, bool Negated)>? Of(IMethodSymbol method, string delegateParameter)
        {
            SyntaxNode? body = null;
            foreach (var reference in method.DeclaringSyntaxReferences)
            {
                body = reference.GetSyntax() switch
                {
                    MethodDeclarationSyntax m => (SyntaxNode?)m.Body ?? m.ExpressionBody?.Expression,
                    LocalFunctionStatementSyntax f => (SyntaxNode?)f.Body ?? f.ExpressionBody?.Expression,
                    _ => null
                };
                if (body != null)
                    break;
            }
            if (body == null)
                return null;

            var flags = new HashSet<string>(method.Parameters
                .Where(p => p.Type.SpecialType == SpecialType.System_Boolean && p.RefKind == RefKind.None)
                .Select(p => p.Name), StringComparer.Ordinal);
            if (flags.Count == 0)
                return Array.Empty<(string, bool)>();

            List<(string Parameter, bool Negated)>? common = null;
            foreach (var name in body.DescendantNodesAndSelf().OfType<IdentifierNameSyntax>())
            {
                var text = name.Identifier.ValueText;
                if (flags.Contains(text) && IsAssigned(name))
                    flags.Remove(text);
                if (text != delegateParameter || name.Parent is MemberAccessExpressionSyntax { Name: var member } && member == name ||
                    name.Parent is NameColonSyntax)
                    continue;

                var site = Invocation(name);
                if (site == null)
                {
                    // `apply != null` decides nothing about when it runs; anything else lets it out.
                    if (ComparedWithNull(name))
                        continue;
                    return Array.Empty<(string, bool)>();
                }

                var clauses = Necessary(site, body, flags);
                if (clauses == null)
                    return Array.Empty<(string, bool)>();
                common = common == null ? clauses : common.Where(clauses.Contains).ToList();
            }

            return common == null
                ? Array.Empty<(string, bool)>()
                : common.Where(c => flags.Contains(c.Parameter)).Distinct().OrderBy(c => c.Parameter, StringComparer.Ordinal).ToList();
        }

        /// <summary>apply(x), apply.Invoke(x) or apply?.Invoke(x): the expression that runs the delegate; null for any other use.</summary>
        static SyntaxNode? Invocation(IdentifierNameSyntax name) => name.Parent switch
        {
            InvocationExpressionSyntax call when call.Expression == name => call,
            MemberAccessExpressionSyntax { Name.Identifier.ValueText: "Invoke", Parent: InvocationExpressionSyntax call } access
                when access.Expression == name && call.Expression == access => call,
            ConditionalAccessExpressionSyntax { WhenNotNull: InvocationExpressionSyntax { Expression: MemberBindingExpressionSyntax { Name.Identifier.ValueText: "Invoke" } } } access
                when access.Expression == name => access,
            _ => null
        };

        static bool ComparedWithNull(IdentifierNameSyntax name) => name.Parent switch
        {
            BinaryExpressionSyntax binary when binary.IsKind(SyntaxKind.EqualsExpression) || binary.IsKind(SyntaxKind.NotEqualsExpression) =>
                (binary.Left == name ? binary.Right : binary.Left).IsKind(SyntaxKind.NullLiteralExpression),
            IsPatternExpressionSyntax pattern => pattern.Expression == name,
            _ => false
        };

        static bool IsAssigned(IdentifierNameSyntax name) =>
            name.Parent is AssignmentExpressionSyntax assignment && assignment.Left == name ||
            name.Parent is ArgumentSyntax argument && !argument.RefKindKeyword.IsKind(SyntaxKind.None);

        /// <summary>
        /// The flags that must hold (or not) for <paramref name="site"/> to run, from it up to the
        /// body. Null when it sits in a lambda or local function: those run when they are called,
        /// which the body does not say.
        /// </summary>
        static List<(string, bool)>? Necessary(SyntaxNode site, SyntaxNode body, HashSet<string> flags)
        {
            var found = new List<(string, bool)>();
            for (SyntaxNode child = site, parent = site.Parent!; child != body && parent != null; child = parent, parent = parent.Parent!)
            {
                switch (parent)
                {
                    case AnonymousFunctionExpressionSyntax or LocalFunctionStatementSyntax:
                        return null;
                    case IfStatementSyntax ifStatement when child == ifStatement.Statement:
                        Flags(ifStatement.Condition, false, flags, found);
                        break;
                    case IfStatementSyntax ifStatement when child == ifStatement.Else:
                        Flags(ifStatement.Condition, true, flags, found);
                        break;
                    case ConditionalExpressionSyntax conditional when child == conditional.WhenTrue:
                        Flags(conditional.Condition, false, flags, found);
                        break;
                    case ConditionalExpressionSyntax conditional when child == conditional.WhenFalse:
                        Flags(conditional.Condition, true, flags, found);
                        break;
                    case BinaryExpressionSyntax binary when child == binary.Right && binary.IsKind(SyntaxKind.LogicalAndExpression):
                        Flags(binary.Left, false, flags, found);
                        break;
                    case BinaryExpressionSyntax binary when child == binary.Right && binary.IsKind(SyntaxKind.LogicalOrExpression):
                        Flags(binary.Left, true, flags, found);
                        break;
                    case BlockSyntax block when child is StatementSyntax statement:
                        // `if (!gate) return self;` before it: in a helper the guards it starts
                        // with are exactly what makes it conditional, so none is skipped.
                        for (var i = block.Statements.IndexOf(statement) - 1; i >= 0; i--)
                            if (TreeWalker.Guard(block.Statements[i]) is { } holds)
                                Flags(((IfStatementSyntax)block.Statements[i]).Condition, !holds, flags, found);
                        break;
                }
            }

            return found;
        }

        /// <summary>The flags a condition pins down: `gate`, `!skip`, `gate &amp;&amp; ready`, `!(a || b)`, `gate == true`.</summary>
        static void Flags(ExpressionSyntax condition, bool negated, HashSet<string> flags, List<(string, bool)> into)
        {
            while (condition is ParenthesizedExpressionSyntax parenthesized)
                condition = parenthesized.Expression;

            switch (condition)
            {
                case PrefixUnaryExpressionSyntax not when not.IsKind(SyntaxKind.LogicalNotExpression):
                    Flags(not.Operand, !negated, flags, into);
                    break;
                case BinaryExpressionSyntax comparison when (comparison.IsKind(SyntaxKind.EqualsExpression) || comparison.IsKind(SyntaxKind.NotEqualsExpression)) &&
                                                            comparison.Right is LiteralExpressionSyntax literal &&
                                                            (literal.IsKind(SyntaxKind.TrueLiteralExpression) || literal.IsKind(SyntaxKind.FalseLiteralExpression)):
                    Flags(comparison.Left, negated ^ (comparison.IsKind(SyntaxKind.EqualsExpression) != literal.IsKind(SyntaxKind.TrueLiteralExpression)), flags, into);
                    break;
                case BinaryExpressionSyntax both when both.IsKind(SyntaxKind.LogicalAndExpression) && !negated:
                case BinaryExpressionSyntax either when either.IsKind(SyntaxKind.LogicalOrExpression) && negated:
                    var binary = (BinaryExpressionSyntax)condition;
                    Flags(binary.Left, negated, flags, into);
                    Flags(binary.Right, negated, flags, into);
                    break;
                case IdentifierNameSyntax name when flags.Contains(name.Identifier.ValueText):
                    into.Add((name.Identifier.ValueText, negated));
                    break;
            }
        }
    }
}
