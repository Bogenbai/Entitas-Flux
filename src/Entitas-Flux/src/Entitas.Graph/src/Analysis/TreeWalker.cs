using System.Collections.Generic;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Entitas.Graph.Analysis
{
    /// <summary>
    /// Walks one syntax tree. Only names the generated API declares are bound (a set lookup
    /// filters the rest), plus invocations — for Destroy(), feature registrations and the call
    /// graph — so a ~2000-file project is a second or two on every core.
    /// </summary>
    sealed class TreeWalker
    {
        readonly BuildContext _context;
        readonly SyntaxTree _tree;
        readonly bool _generated;
        readonly SemanticModel _model;
        readonly string _file;
        readonly TreeResult _result = new TreeResult();
        readonly Dictionary<SyntaxNode, TypeInfo?> _typeCache = new Dictionary<SyntaxNode, TypeInfo?>();
        readonly Dictionary<SyntaxNode, HashSet<string>> _groupFields = new Dictionary<SyntaxNode, HashSet<string>>();
        readonly Dictionary<(IMethodSymbol, int), (ISymbol? Group, string? Loop)> _helperLoops =
            new Dictionary<(IMethodSymbol, int), (ISymbol?, string?)>();

        public TreeWalker(BuildContext context, SyntaxTree tree)
        {
            _context = context;
            _tree = tree;
            _generated = context.IsGenerated(tree);
            _model = context.Full.GetSemanticModel(tree);
            _file = context.RelativePath(tree.FilePath);
        }

        public TreeResult? Walk()
        {
            var root = _tree.GetRoot();
            foreach (var node in root.DescendantNodes())
            {
                switch (node)
                {
                    case SimpleNameSyntax name when _context.GeneratedMemberNames.Contains(name.Identifier.ValueText):
                        VisitName(name);
                        break;
                    case InvocationExpressionSyntax invocation:
                        VisitInvocation(invocation);
                        break;
                    case IdentifierNameSyntax identifier when !_generated:
                        VisitGroupUse(identifier);
                        break;
                }
            }

            return _result.Accesses.Count + _result.Destroys.Count + _result.Calls.Count + _result.Registrations.Count + _result.GroupUses.Count > 0 ? _result : null;
        }

        // -- the generated API ---------------------------------------------------------------

        void VisitName(SimpleNameSyntax name)
        {
            // In generated files only the generated systems and features count (cleanup, event
            // and watched-cleanup systems); the API declaring itself is not an access.
            var accessor = Accessor(name);
            if (accessor == null || (_generated && accessor.Kind == NodeKind.Type))
                return;

            var info = _model.GetSymbolInfo(name);
            var symbol = info.Symbol ?? info.CandidateSymbols.FirstOrDefault();
            if (symbol == null)
                return;

            var classified = _context.Classify(symbol);
            if (classified == null)
                return;

            var (component, contextName, kind) = classified.Value;
            var access = NewAccess(accessor, name, component, contextName);

            switch (kind)
            {
                case ApiKind.Add: Emit(access, Relation.Creates, "Add", name); break;
                case ApiKind.Replace: Emit(access, Relation.Creates, "Replace", name); break;
                case ApiKind.Set: Emit(access, Relation.Creates, "Set", name); break;
                case ApiKind.Remove: Emit(access, Relation.Removes, "Remove", name); break;
                case ApiKind.SafeRemove: Emit(access, Relation.Removes, "SafeRemove", name); break;
                case ApiKind.Has: Emit(access, Relation.Reads, "Has", name); break;
                case ApiKind.Get: Emit(access, Relation.Reads, "Get", name); break;
                case ApiKind.Index:
                    // entity.AddComponent(GameComponentsLookup.X, component) and friends. The entity
                    // is the call's receiver, not the lookup class the index name hangs off.
                    var (call, entity) = IndexCall(name);
                    if (call is "AddComponent" or "ReplaceComponent") Emit(access, Relation.Creates, call, name, entity);
                    else if (call == "RemoveComponent") Emit(access, Relation.Removes, call, name, entity);
                    else Emit(access, Relation.Reads, "Index", name);
                    break;
                case ApiKind.EntityIndex: Emit(access, Relation.Reads, "EntityIndex", name); break;

                case ApiKind.Flag:
                case ApiKind.Atomic:
                    var assignment = AssignmentTarget(name);
                    if (assignment == null)
                    {
                        Emit(access, Relation.Reads, kind == ApiKind.Flag ? "Flag" : "Get", name);
                    }
                    else if (kind == ApiKind.Atomic)
                    {
                        Emit(access, Relation.Creates, "Set", name);
                    }
                    else
                    {
                        var value = _model.GetConstantValue(assignment.Right);
                        if (value.HasValue && value.Value is bool flag)
                        {
                            Emit(access, flag ? Relation.Creates : Relation.Removes, "Flag", name);
                        }
                        else
                        {
                            // isX = someCondition: may set it, may clear it.
                            Emit(access, Relation.Creates, "Flag", name);
                            Emit(Clone(access), Relation.Removes, "Flag", name);
                        }
                    }
                    break;

                case ApiKind.Matcher:
                    var (clause, group) = MatcherClause(name);
                    access.Clause = clause;
                    access.MatcherGroup = group;
                    Emit(access, clause == "NoneOf" ? Relation.Filters : Relation.Reads, clause, name);
                    break;
            }
        }

        void Emit(Access access, string relation, string via, SimpleNameSyntax name, ExpressionSyntax? entity = null)
        {
            access.Relation = relation;
            access.Via = via;

            // Generated companions are folded into the component they serve.
            if (access.Component.ChangeOf != null)
            {
                if (relation != Relation.Reads) return;
                var watched = _context.ComponentByName(access.Component.ChangeOf);
                if (watched == null) return;
                access.Component = watched;
                access.Relation = Relation.Reacts;
            }
            else if (access.Component.ListenerOf != null)
            {
                var observed = _context.ComponentByName(access.Component.ListenerOf);
                if (observed == null || relation != Relation.Creates) return;
                access.Component = observed;
                access.Relation = Relation.Reads;
                access.Via = "Listener";
            }

            if (access.Relation == Relation.Creates || access.Relation == Relation.Removes)
                TraceEntity(access, entity ?? Receiver(name));

            _result.Accesses.Add(access);
        }

        Access NewAccess(TypeInfo accessor, SyntaxNode node, ComponentInfo component, string contextName) => new Access
        {
            Accessor = accessor,
            Method = EnclosingMethod(node),
            Component = component,
            Context = contextName,
            File = _file,
            Line = node.GetLocation().GetLineSpan().StartLinePosition.Line + 1
        };

        static Access Clone(Access a) => new Access
        {
            Accessor = a.Accessor, Method = a.Method, Component = a.Component, Context = a.Context, File = a.File, Line = a.Line
        };

        /// <summary>The call an index constant is passed to, and the entity it is called on (null when unqualified).</summary>
        static (string? Call, ExpressionSyntax? Entity) IndexCall(SimpleNameSyntax name)
        {
            SyntaxNode node = name.Parent is MemberAccessExpressionSyntax access && access.Name == name ? access : name;
            return node.Parent is ArgumentSyntax { Parent: ArgumentListSyntax { Parent: InvocationExpressionSyntax call } }
                ? call.Expression switch
                {
                    MemberAccessExpressionSyntax m => (m.Name.Identifier.ValueText, m.Expression),
                    IdentifierNameSyntax i => (i.Identifier.ValueText, null),
                    _ => default
                }
                : default;
        }

        static AssignmentExpressionSyntax? AssignmentTarget(SimpleNameSyntax name)
        {
            SyntaxNode target = name.Parent is MemberAccessExpressionSyntax access && access.Name == name ? access : name;
            return target.Parent is AssignmentExpressionSyntax assignment && assignment.Left == target ? assignment : null;
        }

        /// <summary>
        /// The clause a matcher property sits in: an argument of AllOf/AnyOf/NoneOf, a
        /// trigger (X.Added()), or a matcher used on its own (GetGroup(GameMatcher.X)). Also the
        /// field or local the resulting group is assigned to, so Destroy() and writes on its
        /// entities can be traced back to it.
        /// </summary>
        (string Clause, ISymbol? Group) MatcherClause(SimpleNameSyntax name)
        {
            SyntaxNode node = name.Parent is MemberAccessExpressionSyntax access && access.Name == name ? access : name;
            var clause = "Matcher";

            if (node.Parent is MemberAccessExpressionSyntax trigger && trigger.Expression == node &&
                trigger.Name.Identifier.ValueText is "Added" or "Removed" or "AddedOrRemoved")
            {
                clause = "Trigger";
            }
            else if (node.Parent is ArgumentSyntax { Parent: ArgumentListSyntax { Parent: InvocationExpressionSyntax call } } &&
                     call.Expression is MemberAccessExpressionSyntax { Name.Identifier.ValueText: var method } &&
                     method is "AllOf" or "AnyOf" or "NoneOf")
            {
                clause = method;
            }

            // Climb to the GetGroup(...) / CreateCollector(...) call, then to what it is assigned to.
            for (var current = node.Parent; current != null; current = current.Parent)
            {
                if (current is StatementSyntax || current is MemberDeclarationSyntax)
                    break;

                if (current is InvocationExpressionSyntax invocation &&
                    invocation.Expression is MemberAccessExpressionSyntax { Name.Identifier.ValueText: "GetGroup" or "CreateCollector" } or
                        IdentifierNameSyntax { Identifier.ValueText: "GetGroup" or "CreateCollector" })
                {
                    return (clause, AssignedSymbol(invocation));
                }
            }

            return (clause, null);
        }

        ISymbol? AssignedSymbol(ExpressionSyntax expression)
        {
            var node = expression.Parent;
            while (node is CastExpressionSyntax || node is ParenthesizedExpressionSyntax)
                node = node.Parent;

            return node switch
            {
                AssignmentExpressionSyntax assignment => _model.GetSymbolInfo(assignment.Left).Symbol,
                EqualsValueClauseSyntax { Parent: VariableDeclaratorSyntax declarator } => _model.GetDeclaredSymbol(declarator),
                ArrowExpressionClauseSyntax { Parent: PropertyDeclarationSyntax property } => _model.GetDeclaredSymbol(property),
                _ => null
            };
        }

        // -- following an entity -----------------------------------------------------------------

        static ExpressionSyntax? Receiver(SimpleNameSyntax name) =>
            name.Parent is MemberAccessExpressionSyntax access && access.Name == name ? access.Expression : null;

        /// <summary>
        /// Where the written entity comes from: a fluent chain or local (the creation site and
        /// the factory that started it), or a foreach over a group — directly, or through the
        /// parameter of a helper the loop passes it to.
        /// </summary>
        void TraceEntity(Access access, ExpressionSyntax? receiver)
        {
            var root = ChainRoot(receiver);
            if (root == null) return;

            switch (root)
            {
                case IdentifierNameSyntax identifier:
                    var symbol = _model.GetSymbolInfo(identifier).Symbol;
                    if (symbol is ILocalSymbol local)
                    {
                        var declaration = local.DeclaringSyntaxReferences.FirstOrDefault()?.GetSyntax();
                        if (declaration is ForEachStatementSyntax forEach)
                        {
                            access.Group = GroupOf(forEach.Expression);
                            access.Loop = LoopKey(forEach);
                            access.EveryEntity = access.Relation == Relation.Removes && RunsForEveryEntity(identifier, forEach);
                        }
                        else if (declaration is VariableDeclaratorSyntax { Initializer.Value: var initializer })
                        {
                            access.Site = SiteKey(access, declaration);
                            access.RootFactory = FactoryOf(ChainRoot(initializer));
                        }
                    }
                    else if (symbol is IParameterSymbol parameter)
                    {
                        (access.Group, access.Loop) = LoopOfArgument(parameter);
                    }
                    break;

                case InvocationExpressionSyntax invocation:
                    access.Site = SiteKey(access, invocation);
                    access.RootFactory = FactoryOf(invocation);
                    break;
            }
        }

        /// <summary>
        /// Whether a write in the body of <paramref name="loop"/> runs for every entity the loop
        /// visits: not under a condition, a nested loop, a catch or a lambda, and not after a
        /// jump that can skip it (<c>if (…) continue;</c>). Asked of removals only: taking X off
        /// every entity of a group of X is what makes X transient, and a conditional RemoveX — a
        /// timer removed once it ran out — leaves entities that keep it.
        /// </summary>
        static bool RunsForEveryEntity(SyntaxNode write, ForEachStatementSyntax loop)
        {
            for (var node = write.Parent; node != null && node != loop; node = node.Parent)
            {
                switch (node)
                {
                    case IfStatementSyntax:
                    case SwitchStatementSyntax:
                    case SwitchExpressionSyntax:
                    case ConditionalExpressionSyntax:
                    case ConditionalAccessExpressionSyntax:
                    case BinaryExpressionSyntax binary when binary.IsKind(SyntaxKind.LogicalAndExpression) ||
                                                            binary.IsKind(SyntaxKind.LogicalOrExpression) ||
                                                            binary.IsKind(SyntaxKind.CoalesceExpression):
                    case CommonForEachStatementSyntax:
                    case ForStatementSyntax:
                    case WhileStatementSyntax:
                    case DoStatementSyntax:
                    case CatchClauseSyntax:
                    case AnonymousFunctionExpressionSyntax:
                    case LocalFunctionStatementSyntax:
                        return false;
                }
            }

            foreach (var node in loop.Statement.DescendantNodes(n => n is not AnonymousFunctionExpressionSyntax and not LocalFunctionStatementSyntax))
            {
                if (node.SpanStart >= write.SpanStart)
                    break;

                switch (node)
                {
                    case ContinueStatementSyntax or BreakStatementSyntax when Leaves(node, loop):
                    case ReturnStatementSyntax or ThrowStatementSyntax or ThrowExpressionSyntax or GotoStatementSyntax:
                    case YieldStatementSyntax yield when yield.IsKind(SyntaxKind.YieldBreakStatement):
                        return false;
                }
            }

            return true;
        }

        /// <summary>Whether a continue/break belongs to <paramref name="loop"/> rather than a loop or switch inside it.</summary>
        static bool Leaves(SyntaxNode jump, ForEachStatementSyntax loop)
        {
            foreach (var ancestor in jump.Ancestors())
            {
                if (ancestor == loop)
                    return true;
                if (ancestor is CommonForEachStatementSyntax or ForStatementSyntax or WhileStatementSyntax or DoStatementSyntax ||
                    (ancestor is SwitchStatementSyntax && jump is BreakStatementSyntax))
                    return false;
            }

            return true;
        }

        static string SiteKey(Access access, SyntaxNode node) => $"{access.File}:{node.SpanStart}";

        string LoopKey(ForEachStatementSyntax forEach) => $"{_file}:{forEach.SpanStart}";

        /// <summary>
        /// A write on a helper's parameter (<c>UnapplyShake(shake)</c> called from
        /// <c>foreach (var shake in _shakes)</c>) lands on whatever entity the callers pass. When
        /// every call in the same type passes the variable of one and the same foreach, the write
        /// is made on that group's entities, in that loop — which is how systems move a loop body
        /// into a method. One level deep, and nothing when the callers disagree: a helper called
        /// from two loops cannot be credited to either.
        /// </summary>
        (ISymbol? Group, string? Loop) LoopOfArgument(IParameterSymbol parameter)
        {
            if (parameter.ContainingSymbol is not IMethodSymbol { MethodKind: MethodKind.Ordinary or MethodKind.LocalFunction } method)
                return default;

            method = method.OriginalDefinition;
            var key = (method, parameter.Ordinal);
            if (_helperLoops.TryGetValue(key, out var cached))
                return cached;

            var declaration = method.DeclaringSyntaxReferences.FirstOrDefault()?.GetSyntax();
            var type = declaration?.SyntaxTree == _tree ? declaration.Ancestors().OfType<TypeDeclarationSyntax>().FirstOrDefault() : null;
            (ISymbol? Group, string? Loop) found = default;
            if (type != null)
            {
                foreach (var call in type.DescendantNodes().OfType<InvocationExpressionSyntax>())
                {
                    var name = call.Expression switch
                    {
                        IdentifierNameSyntax i => i.Identifier.ValueText,
                        MemberAccessExpressionSyntax { Expression: ThisExpressionSyntax } m => m.Name.Identifier.ValueText,
                        _ => null
                    };
                    if (name != method.Name || _model.GetSymbolInfo(call).Symbol is not IMethodSymbol called ||
                        !SymbolEqualityComparer.Default.Equals(called.OriginalDefinition, method))
                        continue;

                    var passed = ArgumentFor(call, parameter);
                    var loop = passed != null && ChainRoot(passed) is IdentifierNameSyntax variable &&
                               _model.GetSymbolInfo(variable).Symbol is ILocalSymbol local &&
                               local.DeclaringSyntaxReferences.FirstOrDefault()?.GetSyntax() is ForEachStatementSyntax forEach
                        ? (Group: GroupOf(forEach.Expression), Loop: LoopKey(forEach))
                        : default;

                    if (loop.Group == null || (found.Loop != null && found.Loop != loop.Loop))
                    {
                        found = default;
                        break;
                    }
                    found = loop;
                }
            }

            _helperLoops[key] = found;
            return found;
        }

        static ExpressionSyntax? ArgumentFor(InvocationExpressionSyntax call, IParameterSymbol parameter)
        {
            var arguments = call.ArgumentList.Arguments;
            var named = arguments.FirstOrDefault(a => a.NameColon?.Name.Identifier.ValueText == parameter.Name);
            if (named != null) return named.Expression;
            return parameter.Ordinal < arguments.Count && arguments[parameter.Ordinal].NameColon == null ? arguments[parameter.Ordinal].Expression : null;
        }

        /// <summary>Strips fluent calls of the generated API (AddX(...).AddY(...)) down to where the entity came from.</summary>
        ExpressionSyntax? ChainRoot(ExpressionSyntax? expression)
        {
            while (expression != null)
            {
                switch (expression)
                {
                    case ParenthesizedExpressionSyntax parenthesized:
                        expression = parenthesized.Expression;
                        continue;
                    case InvocationExpressionSyntax { Expression: MemberAccessExpressionSyntax member } invocation
                        when _context.GeneratedMemberNames.Contains(member.Name.Identifier.ValueText) &&
                             _model.GetSymbolInfo(invocation).Symbol is { } method && _context.Classify(method) != null:
                        expression = member.Expression;
                        continue;
                    default:
                        return expression;
                }
            }

            return null;
        }

        ISymbol? FactoryOf(ExpressionSyntax? expression) =>
            expression is InvocationExpressionSyntax invocation && _model.GetSymbolInfo(invocation).Symbol is IMethodSymbol method &&
            method.DeclaringSyntaxReferences.Length > 0 && !_context.IsGenerated(method.DeclaringSyntaxReferences[0].SyntaxTree)
                ? (method.ReducedFrom ?? method).OriginalDefinition
                : null;

        /// <summary>
        /// foreach (var e in _group) / _group.GetEntities(buffer) / a local holding either →
        /// the field (or local) the group was assigned to.
        /// </summary>
        ISymbol? GroupOf(ExpressionSyntax expression, int depth = 0)
        {
            while (expression is InvocationExpressionSyntax { Expression: MemberAccessExpressionSyntax member })
                expression = member.Expression;
            while (expression is ParenthesizedExpressionSyntax parenthesized)
                expression = parenthesized.Expression;

            var symbol = _model.GetSymbolInfo(expression).Symbol;
            if (symbol is ILocalSymbol local && depth < 3 &&
                local.DeclaringSyntaxReferences.FirstOrDefault()?.GetSyntax() is VariableDeclaratorSyntax { Initializer.Value: var initializer } &&
                initializer is not ObjectCreationExpressionSyntax and not ImplicitObjectCreationExpressionSyntax)
            {
                // List<GameEntity> requests = _requests.GetEntities(_buffer); foreach (var r in requests)
                var origin = GroupOf(initializer, depth + 1);
                if (origin != null) return origin;
            }

            return symbol is IFieldSymbol or ILocalSymbol or IPropertySymbol ? symbol : null;
        }

        /// <summary>
        /// A method of a query (or any non-system type) using one of the type's group fields:
        /// whoever calls the method reads what the group matches, even though the matcher
        /// itself sits in the constructor.
        /// </summary>
        void VisitGroupUse(IdentifierNameSyntax identifier)
        {
            var declaration = identifier.Ancestors().OfType<TypeDeclarationSyntax>().FirstOrDefault();
            if (declaration == null)
                return;

            if (!_groupFields.TryGetValue(declaration, out var names))
            {
                // Syntactic first pass: fields whose declared type is a group.
                names = new HashSet<string>(declaration.Members.OfType<FieldDeclarationSyntax>()
                    .Where(f => f.Declaration.Type.ToString().Contains("Group"))
                    .SelectMany(f => f.Declaration.Variables.Select(v => v.Identifier.ValueText)));
                _groupFields[declaration] = names;
            }

            if (names.Count == 0 || !names.Contains(identifier.Identifier.ValueText))
                return;

            var accessor = Accessor(identifier);
            if (accessor == null || accessor.Kind != NodeKind.Type)
                return;

            var method = EnclosingMethod(identifier);
            if (method == null || method.MethodKind == MethodKind.Constructor)
                return;

            if (_model.GetSymbolInfo(identifier).Symbol is IFieldSymbol field)
                _result.GroupUses.Add(new GroupUse { Method = method, Group = field });
        }

        // -- invocations ----------------------------------------------------------------------

        void VisitInvocation(InvocationExpressionSyntax invocation)
        {
            var accessor = Accessor(invocation);
            if (accessor == null || (_generated && accessor.Kind == NodeKind.Type))
                return;

            // Cheap syntactic pre-checks before binding: this is every call in the project.
            var name = invocation.Expression switch
            {
                MemberAccessExpressionSyntax m => m.Name.Identifier.ValueText,
                IdentifierNameSyntax i => i.Identifier.ValueText,
                GenericNameSyntax g => g.Identifier.ValueText,
                _ => null
            };
            if (name == null)
                return;

            if (_model.GetSymbolInfo(invocation).Symbol is not IMethodSymbol method)
                return;

            if (name == "Destroy" && invocation.ArgumentList.Arguments.Count == 0 && IsEntity(method.ContainingType))
            {
                var destroy = new DestroySite
                {
                    Accessor = accessor,
                    Method = EnclosingMethod(invocation),
                    File = _file,
                    Line = invocation.GetLocation().GetLineSpan().StartLinePosition.Line + 1
                };
                if (invocation.Expression is MemberAccessExpressionSyntax target)
                {
                    var probe = new Access();
                    TraceEntity(probe, target.Expression);
                    destroy.Group = probe.Group;
                }
                _result.Destroys.Add(destroy);
                return;
            }

            // Entitas.Systems.Add, or an override of it (DebugSystems under UNITY_EDITOR).
            if (name == "Add" && accessor.Kind == NodeKind.Feature && invocation.ArgumentList.Arguments.Count == 1 &&
                IsSystemsAdd(method))
            {
                var child = RegisteredType(invocation.ArgumentList.Arguments[0].Expression);
                if (child != null)
                {
                    _result.Registrations.Add(new RegistrationSite
                    {
                        Feature = accessor,
                        Child = child,
                        File = _file,
                        Line = invocation.GetLocation().GetLineSpan().StartLinePosition.Line + 1,
                        Position = invocation.SpanStart
                    });
                }
                return;
            }

            // The call graph: calls into the project's own code.
            var callee = (method.ReducedFrom ?? method).OriginalDefinition;
            var calleeTree = callee.DeclaringSyntaxReferences.FirstOrDefault()?.SyntaxTree;
            if (calleeTree == null && !callee.IsAbstract && callee.ContainingType?.TypeKind != TypeKind.Interface)
                return;
            if (calleeTree != null && _context.IsGenerated(calleeTree))
                return;
            if (callee.ContainingType != null && !callee.ContainingType.Locations.Any(l => l.IsInSource))
                return;

            var caller = EnclosingMethod(invocation);
            if (caller != null)
                _result.Calls.Add(new CallSite { Caller = caller, Callee = callee });
        }

        /// <summary>Add(systems.Create&lt;T&gt;()), Add(new T(...)), or anything statically typed as a system.</summary>
        TypeInfo? RegisteredType(ExpressionSyntax argument)
        {
            if (argument is InvocationExpressionSyntax call &&
                _model.GetSymbolInfo(call).Symbol is IMethodSymbol { TypeArguments.Length: 1 } generic &&
                _context.TypeOf(generic.TypeArguments[0] as INamedTypeSymbol) is { Kind: NodeKind.System or NodeKind.Feature } created)
            {
                return created;
            }

            var type = _model.GetTypeInfo(argument).Type as INamedTypeSymbol;
            return _context.TypeOf(type) is { Kind: NodeKind.System or NodeKind.Feature } typed ? typed : null;
        }

        static bool IsSystemsAdd(IMethodSymbol method)
        {
            for (var current = method; current != null; current = current.OverriddenMethod)
                if (current.ContainingType.ToDisplayString() == "Entitas.Systems") return true;

            for (var type = method.ContainingType; type != null; type = type.BaseType)
                if (type.ToDisplayString() == "Entitas.Systems") return true;

            return false;
        }

        bool IsEntity(INamedTypeSymbol? type)
        {
            for (var current = type; current != null; current = current.BaseType)
                if (SymbolEqualityComparer.Default.Equals(current, _context.EntityBase)) return true;
            return false;
        }

        // -- where we are ---------------------------------------------------------------------

        TypeInfo? Accessor(SyntaxNode node)
        {
            var declaration = node.Ancestors().OfType<BaseTypeDeclarationSyntax>().FirstOrDefault();
            if (declaration == null)
                return null;

            if (!_typeCache.TryGetValue(declaration, out var info))
            {
                info = _model.GetDeclaredSymbol(declaration) is INamedTypeSymbol symbol ? _context.TypeOf(symbol) : null;
                _typeCache[declaration] = info;
            }

            return info;
        }

        IMethodSymbol? EnclosingMethod(SyntaxNode node)
        {
            var symbol = _model.GetEnclosingSymbol(node.SpanStart);
            while (symbol is IMethodSymbol { MethodKind: MethodKind.AnonymousFunction or MethodKind.LocalFunction })
                symbol = symbol.ContainingSymbol;
            return symbol is IMethodSymbol method ? method.OriginalDefinition : null;
        }
    }
}
