using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Entitas.Graph.Analysis
{
    /// <summary>
    /// Where an entity comes from, when that is certain: made right there (a context's
    /// CreateEntity(), or a call to a project method that returns a new one), whatever the callers
    /// of a method pass for one of its parameters, one of a group's entities, or the entity a
    /// primary index finds. Anything else is unknown. It decides which systems reacting to a
    /// [Watched] change can ever see it (<see cref="UnseenReactors"/>), so nothing is guessed: an
    /// entity read from a field, a list or a call into a DLL is unknown.
    /// </summary>
    sealed class EntityOrigin
    {
        public static readonly EntityOrigin Unknown = new EntityOrigin(null, null, null, -1, null);

        /// <summary>A new entity: "file:start-end" of the call making it.</summary>
        public readonly string? Created;

        /// <summary>
        /// For <see cref="Created"/>: the project method that call goes to (CreateEntity.Empty,
        /// ShipFactory.Launch), whose own new entity is the one it returns; null for the
        /// context's CreateEntity() itself.
        /// </summary>
        public readonly IMethodSymbol? Factory;

        /// <summary>What the callers of <see cref="Method"/> pass for its parameter number <see cref="Parameter"/> (an extension method's `this` is 0).</summary>
        public readonly IMethodSymbol? Method;

        public readonly int Parameter;

        /// <summary>An entity of the group assigned to this field or local: the variable of a foreach over it.</summary>
        public readonly ISymbol? Group;

        /// <summary>An entity carrying this component: what `GetEntityWithShipId(id)` returns, when it returns one.</summary>
        public readonly string? Carries;

        EntityOrigin(string? created, IMethodSymbol? factory, IMethodSymbol? method, int parameter, ISymbol? group, string? carries = null)
        {
            Created = created;
            Factory = factory;
            Method = method;
            Parameter = parameter;
            Group = group;
            Carries = carries;
        }

        public static EntityOrigin New(string key, IMethodSymbol? factory) => new EntityOrigin(key, factory, null, -1, null);
        public static EntityOrigin OfParameter(IMethodSymbol method, int parameter) => new EntityOrigin(null, null, method, parameter, null);
        public static EntityOrigin InGroup(ISymbol group) => new EntityOrigin(null, null, null, -1, group);
        public static EntityOrigin Carrying(string component) => new EntityOrigin(null, null, null, -1, null, component);
    }

    /// <summary>A call passing an entity to a project method: whose parameter, and where the entity comes from.</summary>
    sealed class ArgumentSite
    {
        public IMethodSymbol Caller = null!;

        /// <summary>The method as called: an interface's or a base class's, dispatched later.</summary>
        public IMethodSymbol Callee = null!;

        public int Parameter;
        public EntityOrigin Origin = EntityOrigin.Unknown;
    }

    /// <summary>What a project method does with entities, read from its own body.</summary>
    sealed class EntityMethod
    {
        /// <summary>Every return returns the first parameter: `With(this T self, …) { …; return self; }`, a fluent setup helper.</summary>
        public bool ReturnsFirst;

        /// <summary>Every return returns a new entity, made in the method: a factory, and those entities.</summary>
        public IReadOnlyList<EntityOrigin>? Creates;

        /// <summary>The delegate parameters the body runs on the first parameter and nothing else: `set` in `With(self, set) { set(self); … }`.</summary>
        public IReadOnlyCollection<int> RunsOnFirst = Array.Empty<int>();
    }

    /// <summary>The methods being read on the way to an answer: one reached again is a cycle, and what depends on it is not kept.</summary>
    sealed class OriginTrace
    {
        public readonly HashSet<IMethodSymbol> Open = new HashSet<IMethodSymbol>(SymbolEqualityComparer.Default);
        public bool Cycle;
    }

    /// <summary>
    /// <see cref="EntityMethod"/> per method, read once and shared by the tree walkers. A method
    /// is read with a semantic model of its own tree, so only helpers that a write's entity or an
    /// argument actually passes through are bound — With, a Setup… extension, a factory — not
    /// every method of the project.
    /// </summary>
    sealed class EntityMethods
    {
        readonly ConcurrentDictionary<IMethodSymbol, EntityMethod?> _known =
            new ConcurrentDictionary<IMethodSymbol, EntityMethod?>(SymbolEqualityComparer.Default);

        readonly ConcurrentDictionary<SyntaxTree, SemanticModel> _models = new ConcurrentDictionary<SyntaxTree, SemanticModel>();

        /// <summary>Null when the method has no body of the project's here, may be overridden, or is no ordinary method.</summary>
        public EntityMethod? Of(BuildContext context, IMethodSymbol method, OriginTrace trace)
        {
            method = method.OriginalDefinition;
            if (_known.TryGetValue(method, out var known))
                return known;
            if (trace.Open.Contains(method))
            {
                trace.Cycle = true;
                return null;
            }

            // An answer reached through a cycle depends on where the reading started: not kept,
            // so every thread, in whatever order, keeps the same answers.
            var outer = trace.Cycle;
            trace.Cycle = false;
            trace.Open.Add(method);
            var facts = Read(context, method, trace);
            trace.Open.Remove(method);
            var cyclic = trace.Cycle;
            trace.Cycle = outer || cyclic;
            if (!cyclic)
                _known.TryAdd(method, facts);
            return facts;
        }

        EntityMethod? Read(BuildContext context, IMethodSymbol method, OriginTrace trace)
        {
            // A call to a virtual method may land in an override with another body.
            if (method.MethodKind != MethodKind.Ordinary || method.IsAbstract || method.IsVirtual || method.IsOverride || method.IsExtern ||
                method.DeclaringSyntaxReferences.Length != 1)
                return null;
            var reference = method.DeclaringSyntaxReferences[0];
            if (context.IsGenerated(reference.SyntaxTree) || reference.GetSyntax() is not MethodDeclarationSyntax declaration)
                return null;
            SyntaxNode? body = (SyntaxNode?)declaration.Body ?? declaration.ExpressionBody?.Expression;
            if (body == null)
                return null;

            var facts = new EntityMethod { RunsOnFirst = DelegatesRunOnFirst(method, body) };
            var returns = Returns(declaration);
            if (method.ReturnsVoid || returns == null || returns.Count == 0)
                return facts;

            var tree = reference.SyntaxTree;
            var model = _models.GetOrAdd(tree, t => context.Full.GetSemanticModel(t));
            var origins = new EntityOrigins(context, model, context.RelativePath(tree.FilePath), null);
            var found = returns.Select(r => origins.Of(r, trace)).ToList();
            facts.ReturnsFirst = method.Parameters.Length > 0 && method.Parameters[0].RefKind == RefKind.None &&
                                 found.All(o => o.Parameter == 0 && SymbolEqualityComparer.Default.Equals(o.Method, method));
            if (found.All(o => o.Created != null))
                facts.Creates = found;
            return facts;
        }

        /// <summary>What the method returns: its expression body, or every `return x;` outside a lambda or local function; null for an iterator.</summary>
        static List<ExpressionSyntax>? Returns(MethodDeclarationSyntax declaration)
        {
            if (declaration.ExpressionBody != null)
                return new List<ExpressionSyntax> { declaration.ExpressionBody.Expression };

            var returns = new List<ExpressionSyntax>();
            foreach (var node in declaration.Body!.DescendantNodes(n => n is not AnonymousFunctionExpressionSyntax and not LocalFunctionStatementSyntax))
            {
                if (node is YieldStatementSyntax || node is ReturnStatementSyntax { Expression: null })
                    return null;
                if (node is ReturnStatementSyntax { Expression: { } value })
                    returns.Add(value);
            }
            return returns;
        }

        /// <summary>
        /// The delegate parameters every use of which, in the body, is running it on the first
        /// parameter: `set(self)`, `set.Invoke(self)`, `apply?.Invoke(self)`, or a null check.
        /// Under a condition or not — whether it runs is <see cref="HelperConditions"/>' business;
        /// on which entity is this one's. A delegate stored, handed on or run inside a lambda, or
        /// a first parameter assigned, and the helper says nothing. Syntax only, by name: nothing
        /// in a method's own scope may shadow its parameters.
        /// </summary>
        static IReadOnlyCollection<int> DelegatesRunOnFirst(IMethodSymbol method, SyntaxNode body)
        {
            if (method.Parameters.Length < 2 || method.Parameters[0].RefKind != RefKind.None)
                return Array.Empty<int>();
            var first = method.Parameters[0].Name;
            var assigned = EntityOrigins.AssignedNamesIn(body);
            if (assigned.Contains(first))
                return Array.Empty<int>();

            var runs = new List<int>();
            for (var i = 1; i < method.Parameters.Length; i++)
            {
                var parameter = method.Parameters[i];
                if (parameter.Type.TypeKind != TypeKind.Delegate || parameter.RefKind != RefKind.None || assigned.Contains(parameter.Name))
                    continue;

                var uses = 0;
                var only = true;
                foreach (var name in body.DescendantNodesAndSelf().OfType<IdentifierNameSyntax>())
                {
                    if (name.Identifier.ValueText != parameter.Name || name.Parent is MemberAccessExpressionSyntax { Name: var member } && member == name ||
                        name.Parent is NameColonSyntax)
                        continue;
                    if (name.Ancestors().TakeWhile(a => a != body).Any(a => a is AnonymousFunctionExpressionSyntax or LocalFunctionStatementSyntax))
                    {
                        only = false;
                        break;
                    }

                    var call = name.Parent switch
                    {
                        InvocationExpressionSyntax direct when direct.Expression == name => direct,
                        MemberAccessExpressionSyntax { Name.Identifier.ValueText: "Invoke", Parent: InvocationExpressionSyntax invoke } access
                            when access.Expression == name && invoke.Expression == access => invoke,
                        ConditionalAccessExpressionSyntax
                            {
                                WhenNotNull: InvocationExpressionSyntax { Expression: MemberBindingExpressionSyntax { Name.Identifier.ValueText: "Invoke" } } bound
                            } conditional when conditional.Expression == name => bound,
                        _ => null
                    };
                    if (call != null && call.ArgumentList.Arguments.Count == 1 &&
                        call.ArgumentList.Arguments[0] is { RefKindKeyword.RawKind: 0, Expression: IdentifierNameSyntax argument } &&
                        argument.Identifier.ValueText == first)
                    {
                        uses++;
                        continue;
                    }

                    if (name.Parent is BinaryExpressionSyntax comparison &&
                        (comparison.IsKind(SyntaxKind.EqualsExpression) || comparison.IsKind(SyntaxKind.NotEqualsExpression)) &&
                        (comparison.Left.IsKind(SyntaxKind.NullLiteralExpression) || comparison.Right.IsKind(SyntaxKind.NullLiteralExpression)))
                        continue;

                    only = false;
                    break;
                }

                if (only && uses > 0)
                    runs.Add(i);
            }

            return runs;
        }
    }

    /// <summary>
    /// Works out <see cref="EntityOrigin"/>s in one syntax tree with its semantic model. A
    /// receiver is followed down the chain the generated API returns it from (`AddX(…).AddY(…)`),
    /// through fluent helpers returning their first parameter (`.With(…)`, a `Setup…`
    /// extension), a local's only assignment, and a lambda's parameter when the helper it is
    /// passed to runs it on its own receiver (`.With(x =&gt; x.isCat = true)`) — to a creation, a
    /// parameter or a group's foreach, or to nothing known.
    /// </summary>
    sealed class EntityOrigins
    {
        const int MaxSteps = 64;

        readonly BuildContext _context;
        readonly SemanticModel _model;
        readonly string _file;
        readonly Func<ExpressionSyntax, ISymbol?>? _groupOf;
        readonly Dictionary<SyntaxNode, HashSet<string>> _assigned = new Dictionary<SyntaxNode, HashSet<string>>();

        /// <param name="groupOf">The group a foreach runs over (TreeWalker.GroupOf); null where groups do not matter.</param>
        public EntityOrigins(BuildContext context, SemanticModel model, string file, Func<ExpressionSyntax, ISymbol?>? groupOf)
        {
            _context = context;
            _model = model;
            _file = file;
            _groupOf = groupOf;
        }

        public EntityOrigin Of(ExpressionSyntax? expression) => Of(expression, new OriginTrace());

        public EntityOrigin Of(ExpressionSyntax? expression, OriginTrace trace)
        {
            for (var step = 0; expression != null && step < MaxSteps; step++)
            {
                switch (expression)
                {
                    case ParenthesizedExpressionSyntax parenthesized:
                        expression = parenthesized.Expression;
                        continue;

                    case PostfixUnaryExpressionSyntax suppressed when suppressed.IsKind(SyntaxKind.SuppressNullableWarningExpression):
                        expression = suppressed.Operand;
                        continue;

                    case InvocationExpressionSyntax invocation:
                        if (_model.GetSymbolInfo(invocation).Symbol is not IMethodSymbol method)
                            return EntityOrigin.Unknown;

                        // The generated API returns the entity it is called on. A primary index
                        // returns one carrying the indexed component, or null, and then nothing is
                        // written; a context's other members lead to the context, which is no entity.
                        if (invocation.Expression is MemberAccessExpressionSyntax member &&
                            _context.GeneratedMemberNames.Contains(member.Name.Identifier.ValueText) && _context.Classify(method) is { } api)
                        {
                            if (api.Kind == ApiKind.EntityIndex)
                                return IsEntity(method.ReturnType) ? EntityOrigin.Carrying(api.Component.Name) : EntityOrigin.Unknown;
                            expression = member.Expression;
                            continue;
                        }

                        if (IsCreateEntity(method))
                            return EntityOrigin.New(Key(invocation), null);

                        var definition = (method.ReducedFrom ?? method).OriginalDefinition;
                        var facts = _context.EntityMethods.Of(_context, definition, trace);
                        if (facts == null)
                            return EntityOrigin.Unknown;
                        if (facts.ReturnsFirst)
                        {
                            expression = Argument(invocation, method, 0);
                            continue;
                        }
                        return facts.Creates != null ? EntityOrigin.New(Key(invocation), definition) : EntityOrigin.Unknown;

                    case IdentifierNameSyntax identifier:
                        switch (_model.GetSymbolInfo(identifier).Symbol)
                        {
                            case ILocalSymbol local:
                                var declaration = local.DeclaringSyntaxReferences.FirstOrDefault()?.GetSyntax();
                                if (declaration is ForEachStatementSyntax forEach)
                                    return _groupOf?.Invoke(forEach.Expression) is { } group ? EntityOrigin.InGroup(group) : EntityOrigin.Unknown;
                                if (local.RefKind != RefKind.None || declaration is not VariableDeclaratorSyntax { Initializer.Value: var initializer } declarator ||
                                    AssignedNames(Scope(declarator)).Contains(local.Name))
                                    return EntityOrigin.Unknown;
                                expression = initializer;
                                continue;

                            case IParameterSymbol parameter:
                                if (parameter.RefKind != RefKind.None || parameter.ContainingSymbol is not IMethodSymbol owner)
                                    return EntityOrigin.Unknown;
                                if (owner.MethodKind == MethodKind.Ordinary)
                                {
                                    var body = owner.DeclaringSyntaxReferences.FirstOrDefault()?.GetSyntax();
                                    return body == null || AssignedNames(body).Contains(parameter.Name)
                                        ? EntityOrigin.Unknown
                                        : EntityOrigin.OfParameter(owner.OriginalDefinition, parameter.Ordinal);
                                }
                                if (owner.MethodKind != MethodKind.AnonymousFunction)
                                    return EntityOrigin.Unknown;
                                expression = LambdaReceiver(owner, parameter, trace);
                                continue;

                            default:
                                return EntityOrigin.Unknown;
                        }

                    default:
                        return EntityOrigin.Unknown;
                }
            }

            return EntityOrigin.Unknown;
        }

        /// <summary>`x` in `entity.With(x =&gt; …)`: the receiver, when With runs the lambda on it alone; null otherwise.</summary>
        ExpressionSyntax? LambdaReceiver(IMethodSymbol lambda, IParameterSymbol parameter, OriginTrace trace)
        {
            if (parameter.Ordinal != 0 ||
                lambda.DeclaringSyntaxReferences.FirstOrDefault()?.GetSyntax() is not AnonymousFunctionExpressionSyntax syntax ||
                syntax.Parent is not ArgumentSyntax argument || argument.Parent?.Parent is not InvocationExpressionSyntax call ||
                AssignedNames(syntax).Contains(parameter.Name) ||
                _model.GetSymbolInfo(call).Symbol is not IMethodSymbol helper)
                return null;

            var definition = (helper.ReducedFrom ?? helper).OriginalDefinition;
            var ordinal = Ordinal(call, argument, helper);
            var facts = _context.EntityMethods.Of(_context, definition, trace);
            return facts != null && facts.RunsOnFirst.Contains(ordinal) ? Argument(call, helper, 0) : null;
        }

        /// <summary>
        /// For a call into the project, where the entities it passes come from; and whether it
        /// adds components the generated API does not name. Called for every invocation the tree
        /// walker binds.
        /// </summary>
        public void Visit(InvocationExpressionSyntax invocation, IMethodSymbol method, TreeResult result)
        {
            if (AddsUntraced(invocation, method))
                result.UntracedWrites++;

            // The same calls the call graph keeps (TreeWalker.VisitInvocation).
            var callee = (method.ReducedFrom ?? method).OriginalDefinition;
            var tree = callee.DeclaringSyntaxReferences.FirstOrDefault()?.SyntaxTree;
            if (tree == null && !callee.IsAbstract && callee.ContainingType?.TypeKind != TypeKind.Interface ||
                tree != null && _context.IsGenerated(tree) ||
                callee.ContainingType != null && !callee.ContainingType.Locations.Any(l => l.IsInSource))
                return;

            IMethodSymbol? caller = null;
            for (var parameter = 0; parameter < callee.Parameters.Length; parameter++)
            {
                if (!IsEntity(callee.Parameters[parameter].Type))
                    continue;
                caller ??= EnclosingMethod(invocation);
                if (caller == null)
                    return;
                result.Arguments.Add(new ArgumentSite
                {
                    Caller = caller, Callee = callee, Parameter = parameter, Origin = Of(Argument(invocation, method, parameter))
                });
            }
        }

        /// <summary>
        /// entity.AddComponent(index, c) with an index that is no lookup constant, or CopyTo: a
        /// component arrives that no write in the graph names, so none can be said to stay away.
        /// </summary>
        bool AddsUntraced(InvocationExpressionSyntax invocation, IMethodSymbol method)
        {
            if (method.Name is not ("AddComponent" or "ReplaceComponent" or "CopyTo") || method.ContainingNamespace?.ToDisplayString() != "Entitas")
                return false;
            if (method.Name == "CopyTo")
                return true;
            var index = invocation.ArgumentList.Arguments.FirstOrDefault()?.Expression;
            return index == null || _model.GetSymbolInfo(index).Symbol is not { } symbol || _context.Classify(symbol) is not { Kind: ApiKind.Index };
        }

        bool IsEntity(ITypeSymbol type)
        {
            for (var current = type as INamedTypeSymbol; current != null; current = current.BaseType)
                if (SymbolEqualityComparer.Default.Equals(current, _context.EntityBase)) return true;
            return false;
        }

        IMethodSymbol? EnclosingMethod(SyntaxNode node)
        {
            var symbol = _model.GetEnclosingSymbol(node.SpanStart);
            while (symbol is IMethodSymbol { MethodKind: MethodKind.AnonymousFunction or MethodKind.LocalFunction })
                symbol = symbol.ContainingSymbol;
            return symbol is IMethodSymbol method ? method.OriginalDefinition : null;
        }

        /// <summary>Entitas' own context.CreateEntity(): a new entity, with nothing on it.</summary>
        static bool IsCreateEntity(IMethodSymbol method) =>
            method.Name == "CreateEntity" && method.Parameters.Length == 0 &&
            method.ContainingType is { } type && type.OriginalDefinition.Name is "Context" or "IContext" &&
            type.ContainingNamespace?.ToDisplayString() == "Entitas";

        string Key(InvocationExpressionSyntax invocation) => $"{_file}:{invocation.SpanStart}-{invocation.Span.End}";

        /// <summary>The expression a call passes for parameter <paramref name="ordinal"/> of the method's definition; for an extension method called on a receiver, 0 is the receiver.</summary>
        static ExpressionSyntax? Argument(InvocationExpressionSyntax invocation, IMethodSymbol called, int ordinal)
        {
            var definition = called.ReducedFrom ?? called;
            if (ordinal < 0 || ordinal >= definition.Parameters.Length)
                return null;
            var position = ordinal;
            if (called.ReducedFrom != null)
            {
                if (ordinal == 0)
                    return invocation.Expression is MemberAccessExpressionSyntax receiver ? receiver.Expression : null;
                position = ordinal - 1;
            }

            var arguments = invocation.ArgumentList.Arguments;
            var name = definition.Parameters[ordinal].Name;
            var argument = arguments.FirstOrDefault(a => a.NameColon?.Name.Identifier.ValueText == name) ??
                           (position < arguments.Count && arguments.Take(position + 1).All(a => a.NameColon == null) ? arguments[position] : null);
            return argument == null || argument.RefKindKeyword.RawKind != 0 ? null : argument.Expression;
        }

        /// <summary>The parameter of the method's definition an argument is passed for; -1 when unclear.</summary>
        static int Ordinal(InvocationExpressionSyntax invocation, ArgumentSyntax argument, IMethodSymbol called)
        {
            var definition = called.ReducedFrom ?? called;
            if (argument.NameColon != null)
                return definition.Parameters.FirstOrDefault(p => p.Name == argument.NameColon.Name.Identifier.ValueText)?.Ordinal ?? -1;
            var arguments = invocation.ArgumentList.Arguments;
            var position = arguments.IndexOf(argument);
            return arguments.Take(position).Any(a => a.NameColon != null) ? -1 : position + (called.ReducedFrom != null ? 1 : 0);
        }

        /// <summary>Where a local can be assigned again: the method, accessor, local function or lambda declaring it.</summary>
        static SyntaxNode Scope(SyntaxNode declaration) =>
            declaration.Ancestors().FirstOrDefault(a => a is BaseMethodDeclarationSyntax or AccessorDeclarationSyntax or LocalFunctionStatementSyntax or
                AnonymousFunctionExpressionSyntax) ?? declaration.SyntaxTree.GetRoot();

        HashSet<string> AssignedNames(SyntaxNode scope)
        {
            if (!_assigned.TryGetValue(scope, out var names))
                _assigned[scope] = names = AssignedNamesIn(scope);
            return names;
        }

        /// <summary>Names assigned anywhere in <paramref name="scope"/>: `x = …`, `(x, y) = …`, `ref x`, `out x`, `x++`. By name, so a name assigned once anywhere counts as assigned.</summary>
        public static HashSet<string> AssignedNamesIn(SyntaxNode scope)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            void Target(ExpressionSyntax target)
            {
                switch (target)
                {
                    case IdentifierNameSyntax identifier:
                        names.Add(identifier.Identifier.ValueText);
                        break;
                    case TupleExpressionSyntax tuple:
                        foreach (var element in tuple.Arguments)
                            Target(element.Expression);
                        break;
                }
            }

            foreach (var node in scope.DescendantNodes())
            {
                switch (node)
                {
                    case AssignmentExpressionSyntax assignment:
                        Target(assignment.Left);
                        break;
                    case ArgumentSyntax { RefKindKeyword.RawKind: not 0, Expression: IdentifierNameSyntax passed } argument
                        when !argument.RefKindKeyword.IsKind(SyntaxKind.InKeyword):
                        names.Add(passed.Identifier.ValueText);
                        break;
                    case PrefixUnaryExpressionSyntax { Operand: IdentifierNameSyntax operand } prefix
                        when prefix.IsKind(SyntaxKind.PreIncrementExpression) || prefix.IsKind(SyntaxKind.PreDecrementExpression):
                        names.Add(operand.Identifier.ValueText);
                        break;
                    case PostfixUnaryExpressionSyntax { Operand: IdentifierNameSyntax operand } postfix
                        when postfix.IsKind(SyntaxKind.PostIncrementExpression) || postfix.IsKind(SyntaxKind.PostDecrementExpression):
                        names.Add(operand.Identifier.ValueText);
                        break;
                }
            }
            return names;
        }
    }
}
