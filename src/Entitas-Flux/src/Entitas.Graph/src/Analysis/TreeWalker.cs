using System;
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
        readonly Dictionary<ForEachStatementSyntax, bool> _steady = new Dictionary<ForEachStatementSyntax, bool>();
        readonly Dictionary<MemberAccessExpressionSyntax, IFieldSymbol?> _enumMembers = new Dictionary<MemberAccessExpressionSyntax, IFieldSymbol?>();
        readonly Dictionary<SyntaxNode, List<SyntaxNode>?> _suspensions = new Dictionary<SyntaxNode, List<SyntaxNode>?>();
        readonly Dictionary<ForEachStatementSyntax, ISymbol?> _loopGroups = new Dictionary<ForEachStatementSyntax, ISymbol?>();
        readonly EntityOrigins _origins;

        public TreeWalker(BuildContext context, SyntaxTree tree)
        {
            _context = context;
            _tree = tree;
            _generated = context.IsGenerated(tree);
            _model = context.Full.GetSemanticModel(tree);
            _file = context.RelativePath(tree.FilePath);
            _origins = new EntityOrigins(context, _model, _file, e => GroupOf(e));
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
                    case AssignmentExpressionSyntax assignment:
                        NoteWritten(assignment.Left, IsConstruction(assignment));
                        break;
                    case PrefixUnaryExpressionSyntax prefix when prefix.IsKind(SyntaxKind.PreIncrementExpression) || prefix.IsKind(SyntaxKind.PreDecrementExpression):
                        NoteWritten(prefix.Operand, false);
                        break;
                    case PostfixUnaryExpressionSyntax postfix when postfix.IsKind(SyntaxKind.PostIncrementExpression) || postfix.IsKind(SyntaxKind.PostDecrementExpression):
                        NoteWritten(postfix.Operand, false);
                        break;
                    case ArgumentSyntax argument when argument.RefKindKeyword.IsKind(SyntaxKind.RefKeyword) || argument.RefKindKeyword.IsKind(SyntaxKind.OutKeyword):
                        NoteWritten(argument.Expression, false);
                        break;
                    case RefExpressionSyntax reference:
                        NoteWritten(reference.Expression, false);
                        break;
                }
            }

            return _result.Accesses.Count + _result.Destroys.Count + _result.Calls.Count + _result.Registrations.Count + _result.GroupUses.Count +
                   _result.CalledOutsideMethods.Count + _result.Written.Count + _result.UntracedWrites > 0 ? _result : null;
        }

        // -- fields written after construction ----------------------------------------------

        /// <summary>
        /// The name of what <paramref name="target"/> writes, unless it is how an object is built:
        /// its constructor setting its own members, or an object initializer. A member no one
        /// writes otherwise keeps the value it was built with, which is what lets a constant
        /// travel inside an object (<see cref="ConstantArguments"/>). By name, without binding —
        /// this runs on every assignment of the project — so a write to any member of that name
        /// counts: that costs a constant, never soundness.
        /// </summary>
        void NoteWritten(ExpressionSyntax target, bool construction)
        {
            switch (target)
            {
                case ParenthesizedExpressionSyntax parenthesized:
                    NoteWritten(parenthesized.Expression, construction);
                    break;
                case TupleExpressionSyntax tuple:
                    foreach (var element in tuple.Arguments)
                        NoteWritten(element.Expression, false);
                    break;
                case IdentifierNameSyntax identifier when !construction:
                    _result.Written.Add(identifier.Identifier.ValueText);
                    break;
                case MemberAccessExpressionSyntax member when !(construction && member.Expression is ThisExpressionSyntax):
                    _result.Written.Add(member.Name.Identifier.ValueText);
                    break;
                case MemberBindingExpressionSyntax binding:
                    _result.Written.Add(binding.Name.Identifier.ValueText);
                    break;
                case ThisExpressionSyntax when !construction:
                    // `this = other;` in a struct replaces every field.
                    if (target.Ancestors().OfType<TypeDeclarationSyntax>().FirstOrDefault() is { } type)
                        foreach (var field in type.Members.OfType<FieldDeclarationSyntax>().SelectMany(f => f.Declaration.Variables))
                            _result.Written.Add(field.Identifier.ValueText);
                    break;
            }
        }

        /// <summary>
        /// An assignment in an object initializer (`new T { X = 1 }`, `with { X = 1 }`), or a
        /// statement of a constructor (not of a lambda in it, which may run later).
        /// </summary>
        static bool IsConstruction(AssignmentExpressionSyntax assignment)
        {
            if (assignment.Parent is InitializerExpressionSyntax initializer &&
                (initializer.IsKind(SyntaxKind.ObjectInitializerExpression) || initializer.IsKind(SyntaxKind.WithInitializerExpression)))
                return initializer.Parent is BaseObjectCreationExpressionSyntax or WithExpressionSyntax;

            foreach (var ancestor in assignment.Ancestors())
            {
                switch (ancestor)
                {
                    case ConstructorDeclarationSyntax:
                        return true;
                    case AnonymousFunctionExpressionSyntax or LocalFunctionStatementSyntax or MemberDeclarationSyntax or AccessorDeclarationSyntax:
                        return false;
                }
            }

            return false;
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
                    else Emit(access, Relation.Reads, "Index", name, entity);
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
                            access.MayClear = true;
                            var clone = Clone(access);
                            Emit(access, Relation.Creates, "Flag", name);
                            Emit(clone, Relation.Removes, "Flag", name);
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
            {
                TraceEntity(access, entity ?? Receiver(name));
                access.Origin = _origins.Of(entity ?? Receiver(name));
                access.Condition = ConditionOf(name);
                access.Within = OuterLoop(name);
                access.Late = Suspended(name);
            }
            else if (access.Relation == Relation.Reads && access.Clause == null && access.Accessor.Kind == NodeKind.System && !_generated)
            {
                access.ReadGroup = GroupOfEntity(entity ?? Receiver(name));
            }

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
            Accessor = a.Accessor, Method = a.Method, Component = a.Component, Context = a.Context, File = a.File, Line = a.Line, Condition = a.Condition,
            MayClear = a.MayClear
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

        // -- suspension points ---------------------------------------------------------------

        /// <summary>
        /// Whether <paramref name="node"/> may run only after its method was suspended: "an await"
        /// when an await comes before it in an async method, lambda or local function — or in a
        /// loop around it, whose next turn comes after it — "a yield" when a yield return does in
        /// an iterator (a Unity coroutine resumes frames later), null otherwise. Inside a lambda or
        /// local function, any suspension of the method around it counts, wherever it is: a
        /// lambda may be run after the await that follows it. What runs after one happens when
        /// the awaited work finishes, which no feature order tells (EntityViewFactory adding the
        /// view once the asset is loaded). Syntax only, and only for methods that can suspend.
        /// </summary>
        string? Suspended(SyntaxNode node)
        {
            var anywhere = false;
            for (var current = node.Parent; current != null; current = current.Parent)
            {
                var method = current is BaseMethodDeclarationSyntax or AccessorDeclarationSyntax;
                if (!method && current is not (AnonymousFunctionExpressionSyntax or LocalFunctionStatementSyntax))
                {
                    if (current is MemberDeclarationSyntax)
                        return null;
                    continue;
                }

                if (Suspensions(current) is { } points)
                {
                    var found = points.Where(p => anywhere || Before(p, node, current)).ToList();
                    if (found.Count > 0)
                        return found.Any(p => p is not YieldStatementSyntax) ? "an await" : "a yield";
                }
                if (method)
                    return null;
                anywhere = true;
            }
            return null;
        }

        /// <summary>Whether <paramref name="point"/> is passed before <paramref name="node"/> runs: it comes first, or a loop of the method holds both.</summary>
        static bool Before(SyntaxNode point, SyntaxNode node, SyntaxNode method)
        {
            if (point.Span.End <= node.SpanStart)
                return true;
            if (point is CommonForEachStatementSyntax awaited && awaited.Statement.Span.Contains(node.Span))
                return true;
            for (var loop = point.Parent; loop != null && loop != method; loop = loop.Parent)
                if (loop is CommonForEachStatementSyntax or ForStatementSyntax or WhileStatementSyntax or DoStatementSyntax && loop.Span.Contains(node.Span))
                    return true;
            return false;
        }

        /// <summary>
        /// Where a method, lambda or local function may suspend: its awaits (await foreach and
        /// await using too) when it is async, its yield returns. Not those of the functions inside
        /// it. Null for one that cannot suspend — which is decided from its modifiers and return
        /// type before its body is read.
        /// </summary>
        List<SyntaxNode>? Suspensions(SyntaxNode function)
        {
            if (_suspensions.TryGetValue(function, out var known))
                return known;

            var (async, body) = function switch
            {
                MethodDeclarationSyntax m => (m.Modifiers.Any(SyntaxKind.AsyncKeyword), (SyntaxNode?)m.Body ?? m.ExpressionBody),
                LocalFunctionStatementSyntax l => (l.Modifiers.Any(SyntaxKind.AsyncKeyword), (SyntaxNode?)l.Body ?? l.ExpressionBody),
                AnonymousFunctionExpressionSyntax a => (a.AsyncKeyword.IsKind(SyntaxKind.AsyncKeyword), a.Body),
                AccessorDeclarationSyntax x => (false, (SyntaxNode?)x.Body ?? x.ExpressionBody),
                _ => (false, null)
            };
            var iterates = function switch
            {
                MethodDeclarationSyntax m => MayIterate(m.ReturnType),
                LocalFunctionStatementSyntax l => MayIterate(l.ReturnType),
                AccessorDeclarationSyntax { Parent.Parent: BasePropertyDeclarationSyntax property } => MayIterate(property.Type),
                _ => false
            };

            List<SyntaxNode>? points = null;
            if (body != null && (async || iterates))
            {
                foreach (var node in body.DescendantNodes(n => n is not AnonymousFunctionExpressionSyntax and not LocalFunctionStatementSyntax))
                {
                    var suspends = node switch
                    {
                        AwaitExpressionSyntax => async,
                        CommonForEachStatementSyntax loop => async && loop.AwaitKeyword.IsKind(SyntaxKind.AwaitKeyword),
                        UsingStatementSyntax @using => async && @using.AwaitKeyword.IsKind(SyntaxKind.AwaitKeyword),
                        LocalDeclarationStatementSyntax declaration => async && declaration.AwaitKeyword.IsKind(SyntaxKind.AwaitKeyword),
                        YieldStatementSyntax yield => yield.IsKind(SyntaxKind.YieldReturnStatement),
                        _ => false
                    };
                    if (suspends)
                        (points ??= new List<SyntaxNode>()).Add(node);
                }
            }

            _suspensions[function] = points;
            return points;
        }

        /// <summary>IEnumerator, IEnumerable, IAsyncEnumerable, generic or not: a return type an iterator may have.</summary>
        static bool MayIterate(TypeSyntax type)
        {
            var name = type switch
            {
                SimpleNameSyntax simple => simple.Identifier.ValueText,
                QualifiedNameSyntax qualified => qualified.Right.Identifier.ValueText,
                AliasQualifiedNameSyntax alias => alias.Name.Identifier.ValueText,
                _ => string.Empty
            };
            return name.StartsWith("IEnumera", StringComparison.Ordinal) || name.StartsWith("IAsyncEnumera", StringComparison.Ordinal);
        }

        // -- following an entity -----------------------------------------------------------------

        static ExpressionSyntax? Receiver(SimpleNameSyntax name) =>
            name.Parent is MemberAccessExpressionSyntax access && access.Name == name ? access.Expression : null;

        /// <summary>
        /// For a read: the group whose foreach the entity read comes from — directly, or through
        /// the parameter of a helper the loop passes it to (<see cref="LoopOfArgument"/>). Null
        /// for any other entity: one an index finds, a context's unique, a local, a lambda's.
        /// </summary>
        ISymbol? GroupOfEntity(ExpressionSyntax? receiver)
        {
            if (ChainRoot(receiver) is not IdentifierNameSyntax identifier)
                return null;
            switch (_model.GetSymbolInfo(identifier).Symbol)
            {
                case ILocalSymbol local when local.DeclaringSyntaxReferences.FirstOrDefault()?.GetSyntax() is ForEachStatementSyntax forEach:
                    if (!_loopGroups.TryGetValue(forEach, out var group))
                        _loopGroups[forEach] = group = GroupOf(forEach.Expression);
                    return group;
                case IParameterSymbol parameter:
                    return LoopOfArgument(parameter).Group;
                default:
                    return null;
            }
        }

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
                            access.EveryEntity = RunsForEveryEntity(identifier, forEach, access.Relation == Relation.Destroys);
                            access.Steady = IsSteady(forEach);
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
        /// jump that can skip it (<c>if (…) continue;</c>). Taking X off every entity of a group
        /// of X is what makes X transient, and a conditional RemoveX — a timer removed once it ran
        /// out — leaves entities that keep it; destroying every entity, or marking every one for a
        /// destroyer, is what makes a component a message (<see cref="Messages"/>).
        ///
        /// For a Destroy() a `continue` right after another Destroy() of the same entity skips
        /// nothing: `if (!valid) { request.Destroy(); continue; } … request.Destroy();` destroys
        /// every request, which is how handlers turn a request down.
        /// </summary>
        static bool RunsForEveryEntity(SyntaxNode write, ForEachStatementSyntax loop, bool destroy = false)
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
                    case ContinueStatementSyntax when destroy && Leaves(node, loop) && DestroyedBefore(node, loop.Identifier.ValueText):
                        break;
                    case GotoStatementSyntax jump when LandsBefore(jump, write, loop.Statement):
                        break;
                    case ContinueStatementSyntax or BreakStatementSyntax when Leaves(node, loop):
                    case ReturnStatementSyntax or ThrowStatementSyntax or ThrowExpressionSyntax or GotoStatementSyntax:
                    case YieldStatementSyntax yield when yield.IsKind(SyntaxKind.YieldBreakStatement):
                        return false;
                }
            }

            return true;
        }

        /// <summary>
        /// Whether a `goto` jumps forward to a label on the way to <paramref name="write"/>: a
        /// statement of a block holding the write, at or before it. From there the code runs on
        /// to the write, and every jump in between is checked on its own, so the goto skips
        /// nothing: `if (done) goto finish; … finish: request.Destroy();` destroys every request.
        /// </summary>
        static bool LandsBefore(GotoStatementSyntax jump, SyntaxNode write, SyntaxNode body)
        {
            if (!jump.IsKind(SyntaxKind.GotoStatement) || jump.Expression is not IdentifierNameSyntax name)
                return false;
            var label = body.DescendantNodes().OfType<LabeledStatementSyntax>()
                .FirstOrDefault(l => l.Identifier.ValueText == name.Identifier.ValueText);
            return label != null && label.SpanStart > jump.SpanStart && label.SpanStart <= write.SpanStart &&
                   label.Parent is BlockSyntax block && write.Ancestors().Contains(block);
        }

        /// <summary>Whether the block a `continue` ends has already destroyed the loop's entity, in a statement of its own.</summary>
        static bool DestroyedBefore(SyntaxNode jump, string entity) =>
            jump.Parent is BlockSyntax block &&
            block.Statements.TakeWhile(s => s != jump).Any(s => s is ExpressionStatementSyntax
            {
                Expression: InvocationExpressionSyntax
                {
                    ArgumentList.Arguments.Count: 0,
                    Expression: MemberAccessExpressionSyntax { Name.Identifier.ValueText: "Destroy", Expression: IdentifierNameSyntax target }
                }
            } && target.Identifier.ValueText == entity);

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

        /// <summary>
        /// Whether <paramref name="loop"/> runs whenever its system does: it sits in the system's
        /// own Execute() or Cleanup(), not under a condition, inside another loop, a lambda or a
        /// catch. What such a loop does to every entity it visits happens to every entity of its
        /// group, sooner or later. A loop over furniture nested in a loop over sell requests does
        /// it only when someone sells, and one in Initialize() only once.
        ///
        /// Unlike a write's condition, a guard the method starts with counts here, unless it only
        /// asks whether the loop's own group is empty (`if (_requests.count == 0) return;`, which
        /// changes nothing). `if (!_ui.IsOpen&lt;TasksWindow&gt;()) return;` before a loop destroying
        /// hints means the hints live until the window opens.
        ///
        /// The loop may sit in another method of the system instead, when one call to it in the
        /// type is such a statement of Execute() or Cleanup() (`Execute() =&gt; ProcessRequests();`),
        /// one call deep. A base class running an override only sometimes (a pausable system) is
        /// no Execute() of the system's own, and does not count.
        /// </summary>
        bool IsSteady(ForEachStatementSyntax loop)
        {
            if (!_steady.TryGetValue(loop, out var steady))
                _steady[loop] = steady = RunsEveryTime(loop, GroupOf(loop.Expression), helpers: 1);
            return steady;
        }

        /// <summary>Whether <paramref name="statement"/> runs whenever its system's Execute() or Cleanup() does (<see cref="IsSteady"/>).</summary>
        bool RunsEveryTime(SyntaxNode statement, ISymbol? group, int helpers)
        {
            for (var node = statement.Parent; node != null; node = node.Parent)
            {
                if (node is BlockSyntax or TryStatementSyntax or UsingStatementSyntax or LockStatementSyntax or CheckedStatementSyntax or
                    UnsafeStatementSyntax or FixedStatementSyntax or ExpressionStatementSyntax or ArrowExpressionClauseSyntax)
                    continue;
                if (node is not MethodDeclarationSyntax method || _model.GetDeclaredSymbol(method) is not { } symbol || ConditionOf(statement) != null)
                    return false;

                if (method.Body is { } body)
                {
                    var top = statement.AncestorsAndSelf().OfType<StatementSyntax>().First(s => s.Parent == body);
                    if (!body.Statements.TakeWhile(s => s != top)
                            .All(s => Guard(s) == null || group != null && OnlyAsksAbout(((IfStatementSyntax)s).Condition, group)))
                        return false;
                }

                if (_context.RunsEveryFrame(symbol))
                    return true;
                if (helpers == 0 || method.Parent is not TypeDeclarationSyntax type)
                    return false;

                // One call that runs every time is enough: more calls (from Initialize, from a
                // handler) only run it more often.
                return type.DescendantNodes().OfType<InvocationExpressionSyntax>()
                    .Where(call => call.Expression is IdentifierNameSyntax { Identifier.ValueText: var name } && name == symbol.Name ||
                                   call.Expression is MemberAccessExpressionSyntax { Expression: ThisExpressionSyntax, Name.Identifier.ValueText: var member } && member == symbol.Name)
                    .Any(call => SymbolEqualityComparer.Default.Equals(_model.GetSymbolInfo(call).Symbol?.OriginalDefinition, symbol) &&
                                 RunsEveryTime(call, group, helpers - 1));
            }

            return false;
        }

        /// <summary>Whether every name in <paramref name="condition"/> is <paramref name="group"/> or a member of it: `_requests.count == 0`.</summary>
        bool OnlyAsksAbout(ExpressionSyntax condition, ISymbol group) =>
            condition.DescendantNodesAndSelf().OfType<SimpleNameSyntax>()
                .Where(n => !(n.Parent is MemberAccessExpressionSyntax access && access.Name == n))
                .All(n => SymbolEqualityComparer.Default.Equals(_model.GetSymbolInfo(n).Symbol, group));

        // -- conditions ---------------------------------------------------------------------

        /// <summary>
        /// For a helper whose body is not in the compilation (a DLL): parameter names that make a
        /// bool argument the condition of a lambda passed with it — `With(x =&gt; x.isExcluded =
        /// true, when: request.isExcluded)`. A convention, not a guarantee, so the list is short
        /// and literal; matched without case, so `onlyIf` and `OnlyIf` both count. A helper with
        /// a body is read instead (<see cref="HelperConditions"/>), and its names do not matter.
        /// </summary>
        static readonly HashSet<string> ConditionParameters = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "when", "if", "condition", "onlyIf", "predicate"
        };

        /// <summary>
        /// The condition <paramref name="node"/> runs under inside its method, or null when it runs
        /// whenever the method does: every if/else, switch section or arm, ?:, right side of
        /// &amp;&amp;, || or ??, ?., catch, guard — an if before it, in a block enclosing it, that
        /// returns, continues, breaks or jumps — and the bool argument that decides whether a
        /// helper it is passed to as a lambda runs it (<see cref="WhenArguments"/>). Outermost
        /// first, as the code reads: the line cut at the tail loses the innermost detail, not the
        /// case it belongs to (`currencyTypeId == Coins &amp;&amp; amount &gt; 0 &amp;&amp; request.isRefund`).
        ///
        /// Not conditions: loops (a write per entity is what a loop is for; RunsForEveryEntity
        /// covers what that means for removals), lambdas (walked through: a callback runs where it
        /// is passed, under the conditions there), an if that throws (an assertion: what follows it
        /// is the normal path), and the guards a method starts with (<see cref="Leading"/>).
        /// </summary>
        Condition? ConditionOf(SyntaxNode node)
        {
            List<Clause>? clauses = null;
            foreach (var (clause, negate) in Clauses(node))
                (clauses ??= new List<Clause>()).Add(ClauseOf(clause, negate));
            if (clauses == null)
                return null;
            clauses.Reverse();
            return new Condition(clauses);
        }

        /// <summary>The clauses <paramref name="node"/> runs under, innermost first.</summary>
        IEnumerable<(SyntaxNode Node, bool Negate)> Clauses(SyntaxNode node)
        {
            var child = node;
            for (var parent = node.Parent; parent != null && !IsBoundary(parent); child = parent, parent = parent.Parent)
            {
                switch (parent)
                {
                    case IfStatementSyntax ifStatement when child == ifStatement.Statement:
                        yield return (ifStatement, false);
                        break;
                    case IfStatementSyntax ifStatement when child == ifStatement.Else:
                        yield return (ifStatement, true);
                        break;
                    case BlockSyntax block when child is StatementSyntax statement:
                        foreach (var guard in GuardsBefore(block.Statements, block.Statements.IndexOf(statement), IsBoundary(block.Parent!) ? Leading(block.Statements) : 0))
                            yield return guard;
                        break;
                    case SwitchSectionSyntax section when child is StatementSyntax statement:
                        foreach (var guard in GuardsBefore(section.Statements, section.Statements.IndexOf(statement), 0))
                            yield return guard;
                        yield return (section, false);
                        break;
                    case SwitchExpressionArmSyntax arm when child == arm.Expression:
                        yield return (arm, false);
                        break;
                    case ConditionalExpressionSyntax conditional when child == conditional.WhenTrue:
                        yield return (conditional, false);
                        break;
                    case ConditionalExpressionSyntax conditional when child == conditional.WhenFalse:
                        yield return (conditional, true);
                        break;
                    case BinaryExpressionSyntax binary when child == binary.Right && binary.IsKind(SyntaxKind.LogicalAndExpression):
                        yield return (binary, false);
                        break;
                    case BinaryExpressionSyntax binary when child == binary.Right && binary.IsKind(SyntaxKind.LogicalOrExpression):
                        yield return (binary, true);
                        break;
                    case BinaryExpressionSyntax binary when child == binary.Right && binary.IsKind(SyntaxKind.CoalesceExpression):
                        yield return (binary, false);
                        break;
                    case AssignmentExpressionSyntax assignment when child == assignment.Right && assignment.IsKind(SyntaxKind.CoalesceAssignmentExpression):
                        yield return (assignment, false);
                        break;
                    case ConditionalAccessExpressionSyntax access when child == access.WhenNotNull:
                        yield return (access, false);
                        break;
                    case CatchClauseSyntax catchClause when child == catchClause.Block:
                        yield return (catchClause, false);
                        break;
                    case ArgumentSyntax argument when child is AnonymousFunctionExpressionSyntax:
                        // Last argument first: the list is reversed into reading order.
                        foreach (var when in Enumerable.Reverse(WhenArguments(argument)))
                            yield return when;
                        break;
                }
            }
        }

        /// <summary>
        /// For a lambda passed as <paramref name="lambda"/>: the other arguments of the same call
        /// that decide whether the called method runs it, each with whether the lambda runs when
        /// it is false (`Unless(x =&gt; …, skip: c)`). Parameters are resolved through the call's
        /// method, so positional arguments count. When the method's body is in the compilation it
        /// says which (<see cref="HelperConditions"/>); otherwise a bool parameter named as a
        /// condition does (<see cref="ConditionParameters"/>). A constant says nothing: `when: true`.
        /// </summary>
        List<(SyntaxNode Argument, bool Negated)> WhenArguments(ArgumentSyntax lambda)
        {
            var found = new List<(SyntaxNode, bool)>();
            if (lambda.Parent is not ArgumentListSyntax { Arguments.Count: > 1 } list || list.Parent is not InvocationExpressionSyntax call)
                return found;
            var info = _model.GetSymbolInfo(call);
            if ((info.Symbol ?? info.CandidateSymbols.FirstOrDefault()) is not IMethodSymbol method)
                return found;

            var definition = (method.ReducedFrom ?? method).OriginalDefinition;
            var applied = ParameterOf(list, list.Arguments.IndexOf(lambda), method);
            var known = applied == null || _context.IsGenerated(definition.DeclaringSyntaxReferences.FirstOrDefault()?.SyntaxTree)
                ? null
                : _context.HelperConditions.GetOrAdd((definition, applied.Name), key => HelperConditions.Of(key.Method, key.Parameter));

            for (var i = 0; i < list.Arguments.Count; i++)
            {
                var argument = list.Arguments[i];
                if (argument == lambda || ParameterOf(list, i, method) is not { Type.SpecialType: SpecialType.System_Boolean } parameter ||
                    _model.GetConstantValue(argument.Expression).HasValue)
                    continue;

                if (known == null)
                {
                    if (ConditionParameters.Contains(parameter.Name))
                        found.Add((argument, false));
                }
                else
                {
                    foreach (var (name, negated) in known)
                        if (name == parameter.Name)
                            found.Add((argument, negated));
                }
            }

            return found;
        }

        /// <summary>The parameter argument <paramref name="index"/> of a call binds to: by name, or by position while no named argument comes before it.</summary>
        static IParameterSymbol? ParameterOf(ArgumentListSyntax list, int index, IMethodSymbol method)
        {
            var argument = list.Arguments[index];
            return argument.NameColon != null
                ? method.Parameters.FirstOrDefault(p => p.Name == argument.NameColon.Name.Identifier.ValueText)
                : index < method.Parameters.Length && list.Arguments.Take(index).All(a => a.NameColon == null) ? method.Parameters[index] : null;
        }

        static bool IsBoundary(SyntaxNode node) =>
            node is MemberDeclarationSyntax or AccessorDeclarationSyntax or LocalFunctionStatementSyntax;

        /// <summary>The guards among the statements before statement <paramref name="index"/>, nearest first, as clauses on what follows them.</summary>
        static IEnumerable<(SyntaxNode Node, bool Negate)> GuardsBefore(SyntaxList<StatementSyntax> statements, int index, int leading)
        {
            for (var i = index - 1; i >= leading; i--)
                if (Guard(statements[i]) is { } holds && !JumpsTo(statements, i, index, holds))
                    yield return (statements[i], !holds);
        }

        /// <summary>
        /// Whether guard <paramref name="i"/> jumps with a `goto` to a label among the statements
        /// after it, up to statement <paramref name="index"/>: `if (done) goto finish;` guards what
        /// comes before `finish:`, not the statement the label marks or what follows it.
        /// </summary>
        static bool JumpsTo(SyntaxList<StatementSyntax> statements, int i, int index, bool holds)
        {
            var guard = (IfStatementSyntax)statements[i];
            var branch = holds ? guard.Else!.Statement : guard.Statement;
            while (branch is BlockSyntax { Statements.Count: > 0 } block)
                branch = block.Statements[block.Statements.Count - 1];
            if (branch is not GotoStatementSyntax jump || !jump.IsKind(SyntaxKind.GotoStatement) || jump.Expression is not IdentifierNameSyntax name)
                return false;
            for (var k = i + 1; k <= index; k++)
                if (statements[k] is LabeledStatementSyntax label && label.Identifier.ValueText == name.Identifier.ValueText)
                    return true;
            return false;
        }

        /// <summary>
        /// How many statements a method's body starts with that are guards, declarations or plain
        /// expressions. `if (!_pathfinding.IsInitialized) return;` at the top of Execute says when
        /// the system runs at all, not which of its writes happen: every write after it shares it,
        /// so it would mark each line conditional and tell none apart. A guard after a loop, or
        /// after an if with a body of its own, does tell the writes before it from those after.
        /// </summary>
        static int Leading(SyntaxList<StatementSyntax> statements)
        {
            var count = 0;
            while (count < statements.Count &&
                   (statements[count] is LocalDeclarationStatementSyntax or ExpressionStatementSyntax || Guard(statements[count]) != null))
                count++;
            return count;
        }

        /// <summary>
        /// For `if (c) return;` (or continue, break, goto, yield break; alone or last in a block):
        /// false — what follows runs where c does not hold; for `if (c) {…} else return;`: true.
        /// Null for any other statement, an if that throws included.
        /// </summary>
        internal static bool? Guard(StatementSyntax statement)
        {
            if (statement is not IfStatementSyntax ifStatement)
                return null;
            var then = Jumps(ifStatement.Statement);
            var otherwise = ifStatement.Else != null && Jumps(ifStatement.Else.Statement);
            return then == otherwise ? null : otherwise;

            static bool Jumps(StatementSyntax s) => s switch
            {
                ReturnStatementSyntax or ContinueStatementSyntax or BreakStatementSyntax or GotoStatementSyntax => true,
                YieldStatementSyntax yield => yield.IsKind(SyntaxKind.YieldBreakStatement),
                BlockSyntax { Statements.Count: > 0 } b => Jumps(b.Statements[b.Statements.Count - 1]),
                _ => false
            };
        }

        /// <summary>
        /// The clause as text — `amount &lt; 0`, `!(amount &lt; 0)` for an else, `case X` — with the
        /// node it comes from, and for `case X when c` the clause left once X is known to match.
        /// </summary>
        Clause ClauseOf(SyntaxNode clause, bool negate)
        {
            var written = Written(clause).ToList();
            var typed = TypedEnumMembers(written);
            Clause? residual = clause switch
            {
                SwitchSectionSyntax { Labels.Count: 1 } section when section.Labels[0] is CasePatternSwitchLabelSyntax { WhenClause: { } when } =>
                    new Clause(Condition(when.Condition, false, typed), null, false),
                SwitchExpressionArmSyntax { WhenClause: { } when } => new Clause(Condition(when.Condition, false, typed), null, false),
                _ => null
            };
            return new Clause(ClauseText(clause, negate, typed), $"{_file}:{clause.SpanStart}", negate, clause, residual);
        }

        /// <summary>
        /// An enum member is written without its type (`case Worker`, `currencyTypeId == Coins`):
        /// what it is compared with says which enum it is. Not when that leaves two things in the
        /// clause with one name — members of two enums, or a member and a variable — so these
        /// names keep their type. Null when every member can go without.
        /// </summary>
        HashSet<string>? TypedEnumMembers(IEnumerable<SyntaxNode> written)
        {
            Dictionary<string, ITypeSymbol>? members = null;
            HashSet<string>? typed = null;
            HashSet<string>? names = null;
            foreach (var node in written.SelectMany(n => n.DescendantNodesAndSelf()))
            {
                if (node is MemberAccessExpressionSyntax access && EnumMember(access) is { } member)
                {
                    members ??= new Dictionary<string, ITypeSymbol>(StringComparer.Ordinal);
                    if (!members.TryGetValue(member.Name, out var type))
                        members[member.Name] = member.ContainingType;
                    else if (!SymbolEqualityComparer.Default.Equals(type, member.ContainingType))
                        (typed ??= new HashSet<string>(StringComparer.Ordinal)).Add(member.Name);
                }
                else if (node is IdentifierNameSyntax identifier && !(identifier.Parent is MemberAccessExpressionSyntax parent && parent.Name == identifier))
                {
                    (names ??= new HashSet<string>(StringComparer.Ordinal)).Add(identifier.Identifier.ValueText);
                }
            }

            if (members != null && names != null)
                foreach (var name in members.Keys.Where(names.Contains))
                    (typed ??= new HashSet<string>(StringComparer.Ordinal)).Add(name);
            return typed;
        }

        /// <summary>
        /// `Kind.X` (or `Outer.Kind.X`) naming an enum member through its type. Bound only where a
        /// member is accessed on a name that may be a type, and once per node.
        /// </summary>
        IFieldSymbol? EnumMember(MemberAccessExpressionSyntax member)
        {
            if (!(member.Expression is IdentifierNameSyntax { Identifier.ValueText: var name } ? name.Length > 0 && char.IsUpper(name[0])
                    : member.Expression is MemberAccessExpressionSyntax or QualifiedNameSyntax or AliasQualifiedNameSyntax))
                return null;
            if (!_enumMembers.TryGetValue(member, out var field))
            {
                field = _model.GetSymbolInfo(member).Symbol is IFieldSymbol { ContainingType.TypeKind: TypeKind.Enum } symbol &&
                        _model.GetSymbolInfo(member.Expression).Symbol is ITypeSymbol
                    ? symbol
                    : null;
                _enumMembers[member] = field;
            }
            return field;
        }

        /// <summary>The parts of a clause <see cref="ClauseText"/> writes out: an if's condition, not its body.</summary>
        internal static IEnumerable<SyntaxNode> Written(SyntaxNode clause) => clause switch
        {
            ArgumentSyntax argument => new[] { argument.Expression },
            IfStatementSyntax ifStatement => new[] { ifStatement.Condition },
            ConditionalExpressionSyntax conditional => new[] { conditional.Condition },
            BinaryExpressionSyntax binary => new[] { binary.Left },
            AssignmentExpressionSyntax assignment => new[] { assignment.Left },
            ConditionalAccessExpressionSyntax access => new[] { access.Expression },
            CatchClauseSyntax catchClause => catchClause.Declaration == null ? Array.Empty<SyntaxNode>() : new[] { catchClause.Declaration },
            SwitchSectionSyntax section => section.Labels,
            SwitchExpressionArmSyntax arm => arm.WhenClause == null ? new SyntaxNode[] { arm.Pattern } : new SyntaxNode[] { arm.Pattern, arm.WhenClause },
            _ => new[] { clause }
        };

        string ClauseText(SyntaxNode clause, bool negate, HashSet<string>? typed)
        {
            switch (clause)
            {
                case ArgumentSyntax argument:
                    return Condition(argument.Expression, negate, typed);
                case IfStatementSyntax ifStatement:
                    return Condition(ifStatement.Condition, negate, typed);
                case ConditionalExpressionSyntax conditional:
                    return Condition(conditional.Condition, negate, typed);
                case BinaryExpressionSyntax binary when binary.IsKind(SyntaxKind.CoalesceExpression):
                    return Text(binary.Left, typed) + " == null";
                case BinaryExpressionSyntax binary:
                    return Condition(binary.Left, negate, typed);
                case AssignmentExpressionSyntax assignment:
                    return Text(assignment.Left, typed) + " == null";
                case ConditionalAccessExpressionSyntax access:
                    return Text(access.Expression, typed) + " != null";
                case CatchClauseSyntax catchClause:
                    return catchClause.Declaration == null ? "caught an exception" : "caught " + Text(catchClause.Declaration.Type, typed);
                case SwitchSectionSyntax section:
                    return section.Labels.Any(l => l is DefaultSwitchLabelSyntax)
                        ? "default"
                        : "case " + string.Join(" or ", section.Labels.Select(l => l switch
                        {
                            CaseSwitchLabelSyntax value => Text(value.Value, typed),
                            CasePatternSwitchLabelSyntax pattern => Text(pattern.Pattern, typed) +
                                                                     (pattern.WhenClause == null ? string.Empty : " when " + Text(pattern.WhenClause.Condition, typed)),
                            _ => Text(l, typed)
                        }));
                case SwitchExpressionArmSyntax arm:
                    return arm.Pattern is DiscardPatternSyntax && arm.WhenClause == null
                        ? "default"
                        : "case " + Text(arm.Pattern, typed) + (arm.WhenClause == null ? string.Empty : " when " + Text(arm.WhenClause.Condition, typed));
                default:
                    return Text(clause, typed);
            }
        }

        /// <summary>
        /// A condition, negated where the write runs when it does not hold. `x == false` and `!x`
        /// are the negation of x; a negated `==` becomes `!=` and back; a negated `is not P`
        /// becomes `is P`; a name or a call gets a plain `!`; anything else `!(…)`.
        /// </summary>
        string Condition(ExpressionSyntax condition, bool negate, HashSet<string>? typed)
        {
            var expression = Unparenthesized(condition);
            while (true)
            {
                if (expression is PrefixUnaryExpressionSyntax not && not.IsKind(SyntaxKind.LogicalNotExpression))
                {
                    expression = Unparenthesized(not.Operand);
                    negate = !negate;
                }
                else if (expression is BinaryExpressionSyntax comparison &&
                         (comparison.IsKind(SyntaxKind.EqualsExpression) || comparison.IsKind(SyntaxKind.NotEqualsExpression)) &&
                         Unparenthesized(comparison.Right) is LiteralExpressionSyntax literal &&
                         (literal.IsKind(SyntaxKind.TrueLiteralExpression) || literal.IsKind(SyntaxKind.FalseLiteralExpression)))
                {
                    // x == true is x; x == false and x != true are !x.
                    if (comparison.IsKind(SyntaxKind.EqualsExpression) != literal.IsKind(SyntaxKind.TrueLiteralExpression))
                        negate = !negate;
                    expression = Unparenthesized(comparison.Left);
                }
                else
                {
                    break;
                }
            }

            if (!negate)
                return Text(expression, typed);

            return expression switch
            {
                BinaryExpressionSyntax b when b.IsKind(SyntaxKind.EqualsExpression) => $"{Text(b.Left, typed)} != {Text(b.Right, typed)}",
                BinaryExpressionSyntax b when b.IsKind(SyntaxKind.NotEqualsExpression) => $"{Text(b.Left, typed)} == {Text(b.Right, typed)}",
                IsPatternExpressionSyntax { Pattern: UnaryPatternSyntax inner } pattern when inner.IsKind(SyntaxKind.NotPattern) =>
                    $"{Text(pattern.Expression, typed)} is {Text(inner.Pattern, typed)}",
                IdentifierNameSyntax or MemberAccessExpressionSyntax or InvocationExpressionSyntax or ElementAccessExpressionSyntax => "!" + Text(expression, typed),
                _ => $"!({Text(expression, typed)})"
            };
        }

        static ExpressionSyntax Unparenthesized(ExpressionSyntax expression)
        {
            while (expression is ParenthesizedExpressionSyntax parenthesized)
                expression = parenthesized.Expression;
            return expression;
        }

        /// <summary>
        /// The code as written, on one line, with `x == false` as `!x` and `x == true` as `x`
        /// wherever they sit (`order.isProcessed &amp;&amp; !order.isPaid`), and enum members without
        /// their type (`currencyTypeId == Coins`) unless <paramref name="typed"/> names them
        /// (<see cref="TypedEnumMembers"/>). Always, not only when the line is long: one enum
        /// written two ways on two edges of one system reads as two different things.
        /// </summary>
        string Text(SyntaxNode node, HashSet<string>? typed)
        {
            var replacements = new List<(Microsoft.CodeAnalysis.Text.TextSpan Span, string Text)>();
            foreach (var comparison in node.DescendantNodesAndSelf().OfType<BinaryExpressionSyntax>())
            {
                if (!comparison.IsKind(SyntaxKind.EqualsExpression) && !comparison.IsKind(SyntaxKind.NotEqualsExpression) ||
                    Unparenthesized(comparison.Right) is not LiteralExpressionSyntax literal ||
                    !literal.IsKind(SyntaxKind.TrueLiteralExpression) && !literal.IsKind(SyntaxKind.FalseLiteralExpression))
                    continue;
                var operand = Unparenthesized(comparison.Left);
                var holds = comparison.IsKind(SyntaxKind.EqualsExpression) == literal.IsKind(SyntaxKind.TrueLiteralExpression);
                replacements.Add((comparison.Span,
                    holds ? operand.ToString()
                    : operand is IdentifierNameSyntax or MemberAccessExpressionSyntax or InvocationExpressionSyntax or ElementAccessExpressionSyntax ? "!" + operand
                    : $"!({operand})"));
            }

            foreach (var member in node.DescendantNodesAndSelf().OfType<MemberAccessExpressionSyntax>())
                if (EnumMember(member) is { } field && !(typed?.Contains(field.Name) ?? false))
                    replacements.Add((member.Span, member.Name.Identifier.ValueText));

            // Outermost first wins where two overlap; then applied from the end, so the spans before stay valid.
            var applied = new List<(Microsoft.CodeAnalysis.Text.TextSpan Span, string Text)>();
            foreach (var replacement in replacements.OrderByDescending(r => r.Span.Length))
                if (!applied.Any(a => a.Span.OverlapsWith(replacement.Span)))
                    applied.Add(replacement);
            var text = node.ToString();
            foreach (var (span, value) in applied.OrderByDescending(a => a.Span.Start))
                text = text.Substring(0, span.Start - node.SpanStart) + value + text.Substring(span.End - node.SpanStart);

            // A condition spread over lines reads as one.
            var line = new System.Text.StringBuilder(text.Length);
            var space = false;
            foreach (var c in text)
            {
                if (char.IsWhiteSpace(c))
                {
                    space = line.Length > 0;
                    continue;
                }
                if (space)
                    line.Append(' ');
                space = false;
                line.Append(c);
            }
            return line.ToString();
        }

        // -- keys --------------------------------------------------------------------------------

        static string SiteKey(Access access, SyntaxNode node) => $"{access.File}:{node.SpanStart}";

        string LoopKey(ForEachStatementSyntax forEach) => $"{_file}:{forEach.SpanStart}";

        /// <summary>
        /// The outermost foreach whose body holds <paramref name="node"/> in its member, whatever
        /// it walks: a steady loop (<see cref="IsSteady"/>) sits in no other loop, so for anything
        /// in one this is that loop — what a handler's loop does besides consuming its request
        /// (<see cref="Messages"/>), on any entity. Lambdas inside the body count: they are how
        /// fluent helpers take their writes (`entity.With(x =&gt; x.isY = true)`). What the
        /// foreach walks (`_query.Requests()`) is not its body.
        /// </summary>
        string? OuterLoop(SyntaxNode node)
        {
            ForEachStatementSyntax? outer = null;
            for (SyntaxNode? child = node, current = node.Parent; current != null && current is not MemberDeclarationSyntax; child = current, current = current.Parent)
                if (current is ForEachStatementSyntax forEach && child == forEach.Statement)
                    outer = forEach;
            return outer == null ? null : LoopKey(outer);
        }

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
            _origins.Visit(invocation, method, _result);

            if (name == "Destroy" && invocation.ArgumentList.Arguments.Count == 0 && IsEntity(method.ContainingType))
            {
                var destroy = new DestroySite
                {
                    Accessor = accessor,
                    Method = EnclosingMethod(invocation),
                    File = _file,
                    Line = invocation.GetLocation().GetLineSpan().StartLinePosition.Line + 1,
                    Condition = ConditionOf(invocation),
                    Within = OuterLoop(invocation)
                };
                if (invocation.Expression is MemberAccessExpressionSyntax target)
                {
                    var probe = new Access { Relation = Relation.Destroys };
                    TraceEntity(probe, target.Expression);
                    destroy.Group = probe.Group;
                    destroy.Loop = probe.Loop;
                    destroy.EveryEntity = probe.EveryEntity;
                    destroy.Steady = probe.Steady;
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
                _result.Calls.Add(new CallSite
                {
                    Caller = caller, Callee = callee, Condition = ConditionOf(invocation), Syntax = invocation, Within = OuterLoop(invocation),
                    Late = Suspended(invocation)
                });
            else
                _result.CalledOutsideMethods.Add(callee);
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
