using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Operations;

namespace Entitas.Graph.Analysis
{
    enum Truth { Unknown, True, False }

    /// <summary>
    /// A value known at a call: a constant as the compiler folds it (an enum member, a literal, a
    /// const, nameof — converted to the type it is used as, so `case X` and the argument passed
    /// compare alike), or an object built there, with the members it was built with.
    /// </summary>
    sealed class Known
    {
        public readonly object? Constant;

        /// <summary>For an object: its type and the members its constructor or initializer set to known values.</summary>
        public readonly ITypeSymbol? Type;
        public readonly IReadOnlyDictionary<ISymbol, Known>? Members;

        /// <summary>Equal keys, equal values: what bindings are told apart by, in a stable order.</summary>
        public readonly string Key;

        Known(object? constant, ITypeSymbol? type, IReadOnlyDictionary<ISymbol, Known>? members, string key)
        {
            Constant = constant;
            Type = type;
            Members = members;
            Key = key;
        }

        public bool IsObject => Type != null;

        public static Known Of(object? constant) =>
            new Known(constant, null, null, constant == null ? "null" : constant.GetType().Name + ":" + Convert.ToString(constant, CultureInfo.InvariantCulture));

        public static Known Object(ITypeSymbol type, Dictionary<ISymbol, Known> members) =>
            new Known(null, type, members, "new " + type.ToDisplayString() + "{" +
                                         string.Join(",", members.Select(m => Name(m.Key) + "=" + m.Value.Key).OrderBy(s => s, StringComparer.Ordinal)) + "}");

        static string Name(ISymbol member) => member.ContainingType?.Name + "." + member.Name;

        /// <summary>What two values agree on: either, when equal; what two objects of one type share; null when nothing.</summary>
        public static Known? Join(Known a, Known b)
        {
            if (a.Key == b.Key)
                return a;
            if (!a.IsObject || !b.IsObject || !SymbolEqualityComparer.Default.Equals(a.Type, b.Type))
                return null;
            var shared = new Dictionary<ISymbol, Known>(SymbolEqualityComparer.Default);
            foreach (var member in a.Members!)
                if (b.Members!.TryGetValue(member.Key, out var other) && other.Key == member.Value.Key)
                    shared[member.Key] = member.Value;
            return Object(a.Type!, shared);
        }

        /// <summary>`a == b`: decided for two constants of one type, and for an object (never null) against null.</summary>
        public static Truth Equal(Known a, Known b)
        {
            if (a.IsObject || b.IsObject)
            {
                var other = a.IsObject ? b : a;
                return !other.IsObject && other.Constant == null ? Truth.False : Truth.Unknown;
            }
            if (a.Constant == null || b.Constant == null)
                return a.Constant == null && b.Constant == null ? Truth.True : Truth.False;
            if (a.Constant.GetType() != b.Constant.GetType())
                return Truth.Unknown;
            if (a.Constant is double or float)
            {
                var (x, y) = (Convert.ToDouble(a.Constant, CultureInfo.InvariantCulture), Convert.ToDouble(b.Constant, CultureInfo.InvariantCulture));
                return double.IsNaN(x) || double.IsNaN(y) ? Truth.Unknown : x == y ? Truth.True : Truth.False;
            }
            return a.Constant.Equals(b.Constant) ? Truth.True : Truth.False;
        }
    }

    /// <summary>The values a call gives some of a method's parameters, by ordinal.</summary>
    sealed class Bindings
    {
        public static readonly Bindings None = new Bindings(new SortedDictionary<int, Known>());

        readonly SortedDictionary<int, Known> _values;
        public readonly string Key;

        public Bindings(SortedDictionary<int, Known> values)
        {
            _values = values;
            Key = string.Join(";", values.Select(v => v.Key + "=" + v.Value.Key));
        }

        public bool IsEmpty => _values.Count == 0;

        public Known? Of(int ordinal) => _values.TryGetValue(ordinal, out var value) ? value : null;

        public bool Has(int ordinal) => _values.ContainsKey(ordinal);
    }

    /// <summary>
    /// What a call's constant arguments say about the branches of the helper it calls.
    /// `CreateCat(snapshot)` makes a worker's hat list only in `case Worker`; a spawner passing a
    /// snapshot built with `Customer` never gets there, though every write of CreateCat is
    /// credited to it through the call graph. So the argument is followed to a value (a constant,
    /// a parameter of the caller with a value of its own, a local's initializer, what a project
    /// method returns, an object's member set when it was built), and each clause of the helper
    /// testing that parameter is decided: one that cannot hold drops the write for that call, one
    /// that must hold leaves the condition.
    ///
    /// Sound before complete, like every condition here — whatever is not certain is unknown, and
    /// an unknown clause stays as it was:
    /// - a parameter or local counts only when nothing in its method assigns it (by name, at any
    ///   point: a parameter reassigned after the branch costs the constant too);
    /// - a member of an object counts only when nothing writes it but its own type's constructors
    ///   and object initializers (<see cref="TreeWalker"/> collects the names written elsewhere),
    ///   and a constructor gives it a value only by a top-level assignment made once, before any
    ///   statement that might skip it;
    /// - a method's result counts when every `return` in every implementation the call may reach
    ///   gives the same value;
    /// - switch sections and arms are decided in order, an earlier one that may match making a
    ///   later one unknown; `when` stays as the condition when its case matches; a switch with
    ///   `goto case` is left alone.
    /// Calls are crossed at most <see cref="GraphOptions.MaxCallDepth"/> deep, as Propagate's walk is.
    /// </summary>
    sealed class ConstantArguments
    {
        readonly BuildContext _context;
        readonly HashSet<string> _written;
        readonly int _depth;
        readonly Dictionary<SyntaxTree, SemanticModel> _models = new Dictionary<SyntaxTree, SemanticModel>();
        readonly Dictionary<IMethodSymbol, HashSet<string>> _assigned = new Dictionary<IMethodSymbol, HashSet<string>>(SymbolEqualityComparer.Default);
        readonly Dictionary<IMethodSymbol, Dictionary<string, List<ExpressionSyntax>>> _locals =
            new Dictionary<IMethodSymbol, Dictionary<string, List<ExpressionSyntax>>>(SymbolEqualityComparer.Default);
        readonly Dictionary<IMethodSymbol, HashSet<int>> _tested = new Dictionary<IMethodSymbol, HashSet<int>>(SymbolEqualityComparer.Default);
        readonly Dictionary<(SyntaxNode, IMethodSymbol), HashSet<int>> _mentions = new Dictionary<(SyntaxNode, IMethodSymbol), HashSet<int>>(new NodeAndMethod());
        readonly Dictionary<(CallSite, IMethodSymbol, string), Bindings> _bound = new Dictionary<(CallSite, IMethodSymbol, string), Bindings>(new CallAndMethod());
        readonly Dictionary<(IMethodSymbol, string, int), Known?> _returned = new Dictionary<(IMethodSymbol, string, int), Known?>(new MethodBindingsDepth());
        readonly Dictionary<(SyntaxNode, bool, string), (Truth, bool)> _decided = new Dictionary<(SyntaxNode, bool, string), (Truth, bool)>();

        /// <param name="written">Member names written other than while their object is built (TreeWalker.NoteWritten).</param>
        public ConstantArguments(BuildContext context, HashSet<string> written, List<Access> accesses, List<DestroySite> destroys, List<CallSite> calls)
        {
            _context = context;
            _written = written;
            _depth = context.Options.MaxCallDepth;
            FindTested(accesses, destroys, calls);
        }

        // -- which parameters are worth a value -----------------------------------------------

        /// <summary>
        /// The parameters whose values could decide something, per method of a non-system type:
        /// those a clause of its writes or calls mentions (directly or through a local's
        /// initializer), and those it passes on to a method that has such parameters. Only these
        /// are evaluated at calls; everything else stays unbound, and costs nothing.
        /// </summary>
        void FindTested(List<Access> accesses, List<DestroySite> destroys, List<CallSite> calls)
        {
            void Note(IMethodSymbol? method, Condition? condition)
            {
                if (method == null || condition == null || method.Parameters.Length == 0 || _context.TypeOf(method.ContainingType)?.Kind != NodeKind.Type)
                    return;
                foreach (var clause in condition.Clauses.SelectMany(c => c.Parts ?? new[] { c }))
                {
                    if (clause.Node == null)
                        continue;
                    var mentioned = Mentions(method, clause.Node);
                    if (mentioned.Count == 0)
                        continue;
                    if (!_tested.TryGetValue(method, out var tested))
                        _tested[method] = tested = new HashSet<int>();
                    tested.UnionWith(mentioned);
                }
            }

            foreach (var access in accesses)
                if (access.Relation is Relation.Creates or Relation.Removes)
                    Note(access.Method, access.Condition);
            foreach (var destroy in destroys)
                Note(destroy.Method, destroy.Condition);
            foreach (var call in calls)
                Note(call.Caller, call.Condition);
            if (_tested.Count == 0)
                return;

            var relaying = calls.Where(c => c.Syntax != null && c.Caller.Parameters.Length > 0 && _context.TypeOf(c.Caller.ContainingType)?.Kind == NodeKind.Type).ToList();
            for (var changed = true; changed;)
            {
                changed = false;
                foreach (var call in relaying)
                {
                    if (!_context.Dispatch(new[] { call.Callee }).Any(m => _tested.ContainsKey(m)))
                        continue;
                    var passed = Mentions(call.Caller, call.Syntax!);
                    if (passed.Count == 0)
                        continue;
                    if (!_tested.TryGetValue(call.Caller, out var tested))
                        _tested[call.Caller] = tested = new HashSet<int>();
                    var count = tested.Count;
                    tested.UnionWith(passed);
                    changed |= tested.Count != count;
                }
            }
        }

        /// <summary>The parameters of <paramref name="method"/> a clause or call names, directly or through the initializer of a local (two deep).</summary>
        HashSet<int> Mentions(IMethodSymbol method, SyntaxNode node)
        {
            if (_mentions.TryGetValue((node, method), out var found))
                return found;
            found = new HashSet<int>();
            var parameters = method.Parameters.ToDictionary(p => p.Name, p => p.Ordinal, StringComparer.Ordinal);
            var locals = Locals(method);

            void Scan(SyntaxNode part, int depth)
            {
                foreach (var name in part.DescendantNodesAndSelf().OfType<IdentifierNameSyntax>())
                {
                    if (name.Parent is MemberAccessExpressionSyntax access && access.Name == name || name.Parent is NameColonSyntax)
                        continue;
                    var text = name.Identifier.ValueText;
                    if (parameters.TryGetValue(text, out var ordinal))
                        found.Add(ordinal);
                    else if (depth < 2 && locals.TryGetValue(text, out var initializers))
                        foreach (var initializer in initializers)
                            Scan(initializer, depth + 1);
                }
            }

            foreach (var part in Subjects(node))
                Scan(part, 0);
            _mentions[(node, method)] = found;
            return found;
        }

        /// <summary>What a clause tests: an if's condition, a switch's value and the section's labels; a call as a whole.</summary>
        static IEnumerable<SyntaxNode> Subjects(SyntaxNode node) => node switch
        {
            SwitchSectionSyntax { Parent: SwitchStatementSyntax switchStatement } section => new SyntaxNode[] { switchStatement.Expression }.Concat(section.Labels),
            SwitchExpressionArmSyntax { Parent: SwitchExpressionSyntax switchExpression } arm => new SyntaxNode[] { switchExpression.GoverningExpression }.Concat(TreeWalker.Written(arm)),
            IfStatementSyntax or ConditionalExpressionSyntax or ArgumentSyntax => TreeWalker.Written(node),
            BinaryExpressionSyntax binary when binary.IsKind(SyntaxKind.LogicalAndExpression) || binary.IsKind(SyntaxKind.LogicalOrExpression) => TreeWalker.Written(node),
            InvocationExpressionSyntax => new[] { node },
            _ => Array.Empty<SyntaxNode>()
        };

        Dictionary<string, List<ExpressionSyntax>> Locals(IMethodSymbol method)
        {
            if (!_locals.TryGetValue(method, out var locals))
            {
                locals = new Dictionary<string, List<ExpressionSyntax>>(StringComparer.Ordinal);
                foreach (var reference in method.DeclaringSyntaxReferences)
                foreach (var declarator in reference.GetSyntax().DescendantNodes().OfType<VariableDeclaratorSyntax>())
                {
                    if (declarator.Initializer == null)
                        continue;
                    if (!locals.TryGetValue(declarator.Identifier.ValueText, out var list))
                        locals[declarator.Identifier.ValueText] = list = new List<ExpressionSyntax>();
                    list.Add(declarator.Initializer.Value);
                }
                _locals[method] = locals;
            }
            return locals;
        }

        // -- binding a call ---------------------------------------------------------------------

        /// <summary>
        /// The values <paramref name="call"/> gives the parameters of <paramref name="callee"/> (one
        /// of the methods it may land in) that something there tests, its caller's own parameters
        /// having <paramref name="caller"/>.
        /// </summary>
        public Bindings Bind(CallSite call, IMethodSymbol callee, Bindings caller)
        {
            if (call.Syntax == null || !_tested.TryGetValue(callee, out var wanted))
                return Bindings.None;
            var key = (call, callee, caller.Key);
            if (_bound.TryGetValue(key, out var bound))
                return bound;

            bound = Bindings.None;
            if (MayCarry(call, caller) && Model(call.Syntax.SyntaxTree).GetOperation(call.Syntax) is IInvocationOperation invocation)
            {
                var values = new SortedDictionary<int, Known>();
                foreach (var argument in invocation.Arguments)
                {
                    if (argument.Parameter is not { } parameter || argument.ArgumentKind == ArgumentKind.ParamArray || !wanted.Contains(parameter.Ordinal) ||
                        parameter.Ordinal >= callee.Parameters.Length || callee.Parameters[parameter.Ordinal].RefKind is RefKind.Ref or RefKind.Out)
                        continue;
                    if (Value(argument.Value, call.Caller, caller, 0) is { } value)
                        values[parameter.Ordinal] = value;
                }
                if (values.Count > 0)
                    bound = new Bindings(values);
            }

            _bound[key] = bound;
            return bound;
        }

        /// <summary>
        /// Whether any argument of the call (or what it is called on) could hold a known value:
        /// a literal, a name that may be a constant or a type (`Big`, `Kind.One`), a parameter the
        /// caller has a value for, a local whose initializer could, an object built there, or a
        /// call given any of these. Checked on the syntax before anything is bound — binding the
        /// caller's body is what a value costs, and most calls pass nothing of the kind. A
        /// constant only a method's result returns, with nothing known passed in, is missed.
        /// </summary>
        bool MayCarry(CallSite call, Bindings caller)
        {
            var parameters = call.Caller.Parameters;
            var locals = Locals(call.Caller);

            bool May(ExpressionSyntax expression, int depth)
            {
                switch (expression)
                {
                    case LiteralExpressionSyntax or BaseObjectCreationExpressionSyntax or DefaultExpressionSyntax:
                        return true;
                    case ParenthesizedExpressionSyntax parenthesized:
                        return May(parenthesized.Expression, depth);
                    case CastExpressionSyntax cast:
                        return May(cast.Expression, depth);
                    case PrefixUnaryExpressionSyntax unary:
                        return May(unary.Operand, depth);
                    case IdentifierNameSyntax identifier:
                        var name = identifier.Identifier.ValueText;
                        if (parameters.FirstOrDefault(p => p.Name == name) is { } parameter)
                            return caller.Has(parameter.Ordinal);
                        if (locals.TryGetValue(name, out var initializers))
                            return depth < 3 && initializers.Any(i => May(i, depth + 1));
                        return name.Length > 0 && char.IsUpper(name[0]);
                    case MemberAccessExpressionSyntax member:
                        return May(member.Expression, depth);
                    case InvocationExpressionSyntax invocation:
                        return invocation.Expression is IdentifierNameSyntax { Identifier.ValueText: "nameof" } ||
                               invocation.ArgumentList.Arguments.Any(a => May(a.Expression, depth)) ||
                               invocation.Expression is MemberAccessExpressionSyntax receiver && May(receiver.Expression, depth);
                    default:
                        return false;
                }
            }

            // What it is called on matters as the `this` of an extension method; a bare type name
            // (`Factory.Make(…)`) carries nothing.
            return call.Syntax!.ArgumentList.Arguments.Any(a => May(a.Expression, 0)) ||
                   call.Syntax.Expression is MemberAccessExpressionSyntax { Expression: not IdentifierNameSyntax } target && May(target.Expression, 0) ||
                   call.Syntax.Expression is MemberAccessExpressionSyntax { Expression: IdentifierNameSyntax receiver } &&
                   (parameters.Any(p => p.Name == receiver.Identifier.ValueText) || locals.ContainsKey(receiver.Identifier.ValueText)) && May(receiver, 0);
        }

        // -- deciding clauses -------------------------------------------------------------------

        /// <summary>
        /// Whether <paramref name="condition"/>, written in <paramref name="method"/>, can hold when
        /// its parameters have <paramref name="bindings"/>. False when one of its clauses cannot;
        /// otherwise true, with <paramref name="rest"/> what is still open: a clause that must hold
        /// goes, `case X when c` whose X matches leaves `c`, the rest stays. Null rest: no condition.
        /// </summary>
        public bool Holds(Condition? condition, IMethodSymbol? method, Bindings bindings, out Condition? rest)
        {
            rest = condition;
            if (condition == null || method == null || bindings.IsEmpty)
                return true;

            List<Clause>? kept = null;
            for (var i = 0; i < condition.Clauses.Count; i++)
            {
                var clause = condition.Clauses[i];
                var (truth, guarded) = Decide(clause, method, bindings);
                if (truth == Truth.False)
                    return false;
                if (truth == Truth.Unknown || guarded && clause.Residual == null)
                {
                    kept?.Add(clause);
                    continue;
                }

                kept ??= condition.Clauses.Take(i).ToList();
                if (guarded)
                    kept.Add(clause.Residual!);
            }

            if (kept != null)
                rest = kept.Count == 0 ? null : new Condition(kept);
            return true;
        }

        /// <summary>
        /// A clause under bindings; for a case that matches, whether a `when` still decides (true:
        /// only the guard is left). A jump's `!(a &amp;&amp; b)` (<see cref="Clause.Parts"/>) holds
        /// when one of its parts cannot, cannot when all must, and is unknown otherwise.
        /// </summary>
        (Truth Truth, bool Guarded) Decide(Clause clause, IMethodSymbol method, Bindings bindings)
        {
            if (clause.Parts != null)
            {
                var all = Truth.True;
                foreach (var part in clause.Parts)
                {
                    var (truth, _) = Decide(part, method, bindings);
                    if (truth == Truth.False)
                        return (Truth.True, false);
                    if (truth == Truth.Unknown)
                        all = Truth.Unknown;
                }
                return (all == Truth.True ? Truth.False : Truth.Unknown, false);
            }

            var node = clause.Node;
            if (node == null || !Mentions(method, node).Any(bindings.Has))
                return (Truth.Unknown, false);
            // A clause's node lies in one method, so the node and the values decide it.
            var key = (node, clause.Negated, bindings.Key);
            if (_decided.TryGetValue(key, out var decided))
                return decided;

            var model = Model(node.SyntaxTree);
            decided = node switch
            {
                IfStatementSyntax ifStatement => (Flip(Test(model.GetOperation(ifStatement.Condition), method, bindings), clause.Negated), false),
                ConditionalExpressionSyntax conditional => (Flip(Test(model.GetOperation(conditional.Condition), method, bindings), clause.Negated), false),
                ArgumentSyntax argument => (Flip(Test(model.GetOperation(argument.Expression), method, bindings), clause.Negated), false),
                BinaryExpressionSyntax binary when binary.IsKind(SyntaxKind.LogicalAndExpression) || binary.IsKind(SyntaxKind.LogicalOrExpression) =>
                    (Flip(Test(model.GetOperation(binary.Left), method, bindings), clause.Negated), false),
                SwitchSectionSyntax section => Section(section, model, method, bindings),
                SwitchExpressionArmSyntax arm => Arm(arm, model, method, bindings),
                _ => (Truth.Unknown, false)
            };
            _decided[key] = decided;
            return decided;
        }

        /// <summary>
        /// A section of a switch statement. Taken when one of its labels matches and no earlier
        /// section took the value; a default section when no other section's label matches.
        /// </summary>
        (Truth, bool) Section(SwitchSectionSyntax section, SemanticModel model, IMethodSymbol method, Bindings bindings)
        {
            if (section.Parent is not SwitchStatementSyntax switchStatement ||
                switchStatement.DescendantNodes().OfType<GotoStatementSyntax>().Any(g => !g.IsKind(SyntaxKind.GotoStatement)) ||
                model.GetOperation(switchStatement) is not ISwitchOperation operation ||
                Value(operation.Value, method, bindings, 0) is not { } value)
                return (Truth.Unknown, false);

            (Truth Match, Truth Guard) Label(ICaseClauseOperation label) => label switch
            {
                ISingleValueCaseClauseOperation single => (Constant(single.Value) is { } constant ? Known.Equal(value, constant) : Truth.Unknown, Truth.True),
                IPatternCaseClauseOperation pattern => (Match(value, pattern.Pattern),
                    pattern.Guard == null ? Truth.True : Test(pattern.Guard, method, bindings)),
                _ => (Truth.Unknown, Truth.Unknown)
            };

            var target = operation.Cases.FirstOrDefault(c => c.Syntax == section);
            if (target == null)
                return (Truth.Unknown, false);

            // Any other section's label: does it certainly take the value, certainly not, or may it?
            Truth Others(IEnumerable<ISwitchCaseOperation> cases)
            {
                var result = Truth.False;
                foreach (var label in cases.SelectMany(c => c.Clauses).Where(l => l is not IDefaultCaseClauseOperation))
                {
                    var (match, guard) = Label(label);
                    var taken = And(match, guard);
                    if (taken == Truth.True)
                        return Truth.True;
                    if (taken == Truth.Unknown)
                        result = Truth.Unknown;
                }
                return result;
            }

            var own = Truth.False;
            var guarded = 0;
            foreach (var label in target.Clauses.Where(l => l is not IDefaultCaseClauseOperation))
            {
                var (match, guard) = Label(label);
                if (match == Truth.True && guard == Truth.Unknown)
                    guarded++;
                own = Or(own, And(match, guard));
            }

            if (target.Clauses.Any(l => l is IDefaultCaseClauseOperation))
            {
                // Default is position-free: taken when no other section matches.
                var fallback = Not(Others(operation.Cases.Where(c => c != target)));
                return guarded > 0 ? (Or(own, fallback) == Truth.True ? Truth.True : Truth.Unknown, false) : (Or(own, fallback), false);
            }

            var before = Others(operation.Cases.TakeWhile(c => c != target));
            if (before == Truth.True || own == Truth.False)
                return (Truth.False, false);
            if (before == Truth.Unknown)
                return (Truth.Unknown, false);
            if (own == Truth.True)
                return (Truth.True, false);
            // Unknown only because of its own guard: the case matches, the `when` decides.
            return guarded == 1 && target.Clauses.Length == 1 ? (Truth.True, true) : (Truth.Unknown, false);
        }

        /// <summary>An arm of a switch expression: taken when its pattern and guard hold and no earlier arm's did.</summary>
        (Truth, bool) Arm(SwitchExpressionArmSyntax arm, SemanticModel model, IMethodSymbol method, Bindings bindings)
        {
            if (arm.Parent is not SwitchExpressionSyntax switchExpression ||
                model.GetOperation(switchExpression) is not ISwitchExpressionOperation operation ||
                Value(operation.Value, method, bindings, 0) is not { } value)
                return (Truth.Unknown, false);

            var before = Truth.False;
            foreach (var candidate in operation.Arms)
            {
                var match = Match(value, candidate.Pattern);
                var guard = candidate.Guard == null ? Truth.True : Test(candidate.Guard, method, bindings);
                if (candidate.Syntax != arm)
                {
                    var taken = And(match, guard);
                    if (taken == Truth.True)
                        return (Truth.False, false);
                    if (taken == Truth.Unknown)
                        before = Truth.Unknown;
                    continue;
                }

                if (match == Truth.False || guard == Truth.False)
                    return (Truth.False, false);
                if (before == Truth.Unknown || match == Truth.Unknown)
                    return (Truth.Unknown, false);
                return guard == Truth.True ? (Truth.True, false) : (Truth.True, true);
            }

            return (Truth.Unknown, false);
        }

        /// <summary>
        /// A bool condition: constants, !, &amp;&amp;, ||, == and != of known values, `is` patterns.
        /// <paramref name="depth"/> as in <see cref="Value"/>: a condition inside a method's
        /// result counts the calls crossed to get there.
        /// </summary>
        Truth Test(IOperation? operation, IMethodSymbol method, Bindings bindings, int depth = 0)
        {
            if (operation == null)
                return Truth.Unknown;
            if (operation.ConstantValue is { HasValue: true, Value: bool constant })
                return constant ? Truth.True : Truth.False;

            switch (operation)
            {
                case IUnaryOperation { OperatorKind: UnaryOperatorKind.Not, OperatorMethod: null } not:
                    return Not(Test(not.Operand, method, bindings, depth));
                case IBinaryOperation binary when binary.OperatorMethod == null || binary.OperatorMethod.ContainingType.SpecialType == SpecialType.System_String:
                    switch (binary.OperatorKind)
                    {
                        case BinaryOperatorKind.ConditionalAnd:
                            return And(Test(binary.LeftOperand, method, bindings, depth), Test(binary.RightOperand, method, bindings, depth));
                        case BinaryOperatorKind.ConditionalOr:
                            return Or(Test(binary.LeftOperand, method, bindings, depth), Test(binary.RightOperand, method, bindings, depth));
                        case BinaryOperatorKind.Equals:
                        case BinaryOperatorKind.NotEquals:
                            var equal = Value(binary.LeftOperand, method, bindings, depth) is { } left && Value(binary.RightOperand, method, bindings, depth) is { } right
                                ? Known.Equal(left, right)
                                : Truth.Unknown;
                            return binary.OperatorKind == BinaryOperatorKind.Equals ? equal : Not(equal);
                    }
                    return Truth.Unknown;
                case IIsPatternOperation pattern:
                    return Value(pattern.Value, method, bindings, depth) is { } tested ? Match(tested, pattern.Pattern) : Truth.Unknown;
                default:
                    return Value(operation, method, bindings, depth) is { IsObject: false, Constant: bool known } ? known ? Truth.True : Truth.False : Truth.Unknown;
            }
        }

        /// <summary>Whether <paramref name="value"/> matches a pattern: constants, `or`, `and`, `not`, `_` and `var`; anything else is unknown.</summary>
        static Truth Match(Known value, IPatternOperation pattern) => pattern switch
        {
            IConstantPatternOperation constant => Constant(constant.Value) is { } known ? Known.Equal(value, known) : Truth.Unknown,
            INegatedPatternOperation negated => Not(Match(value, negated.Pattern)),
            IBinaryPatternOperation { OperatorKind: BinaryOperatorKind.Or } either => Or(Match(value, either.LeftPattern), Match(value, either.RightPattern)),
            IBinaryPatternOperation { OperatorKind: BinaryOperatorKind.And } both => And(Match(value, both.LeftPattern), Match(value, both.RightPattern)),
            IDiscardPatternOperation => Truth.True,
            IDeclarationPatternOperation { MatchesNull: true } => Truth.True,
            _ => Truth.Unknown
        };

        static Known? Constant(IOperation operation) => operation.ConstantValue.HasValue ? Known.Of(operation.ConstantValue.Value) : null;

        static Truth Not(Truth t) => t == Truth.True ? Truth.False : t == Truth.False ? Truth.True : Truth.Unknown;
        static Truth Flip(Truth t, bool negated) => negated ? Not(t) : t;
        static Truth And(Truth a, Truth b) => a == Truth.False || b == Truth.False ? Truth.False : a == Truth.True && b == Truth.True ? Truth.True : Truth.Unknown;
        static Truth Or(Truth a, Truth b) => a == Truth.True || b == Truth.True ? Truth.True : a == Truth.False && b == Truth.False ? Truth.False : Truth.Unknown;

        // -- following a value ------------------------------------------------------------------

        /// <summary>
        /// The value of <paramref name="operation"/>, written in <paramref name="method"/> whose
        /// parameters have <paramref name="bindings"/>; null when it is not certain.
        /// <paramref name="depth"/> counts the methods and constructors crossed to get here.
        /// </summary>
        Known? Value(IOperation? operation, IMethodSymbol method, Bindings bindings, int depth)
        {
            if (operation == null)
                return null;
            if (operation.ConstantValue.HasValue)
                return Known.Of(operation.ConstantValue.Value);

            switch (operation)
            {
                // Conversions that keep the value: identity, implicit reference or nullable, and
                // the one a target-typed `new()` comes wrapped in (to its own type).
                case IConversionOperation conversion when !conversion.Conversion.IsUserDefined &&
                                                          (conversion.Conversion.IsIdentity ||
                                                           conversion.Conversion.IsImplicit && (conversion.Conversion.IsReference || conversion.Conversion.IsNullable) ||
                                                           conversion.Operand is IObjectCreationOperation && SymbolEqualityComparer.Default.Equals(conversion.Type, conversion.Operand.Type)):
                    return Value(conversion.Operand, method, bindings, depth);

                case IParameterReferenceOperation reference:
                    var parameter = reference.Parameter;
                    return SymbolEqualityComparer.Default.Equals(parameter.ContainingSymbol?.OriginalDefinition, method.OriginalDefinition) &&
                           parameter.RefKind is RefKind.None or RefKind.In && !Assigned(method).Contains(parameter.Name)
                        ? bindings.Of(parameter.Ordinal)
                        : null;

                case ILocalReferenceOperation reference:
                    var local = reference.Local;
                    if (local.IsRef || Assigned(method).Contains(local.Name) ||
                        local.DeclaringSyntaxReferences.FirstOrDefault()?.GetSyntax() is not VariableDeclaratorSyntax { Initializer: { } initializer } declarator ||
                        !method.DeclaringSyntaxReferences.Any(r => r.SyntaxTree == declarator.SyntaxTree && r.Span.Contains(declarator.Span)))
                        return null;
                    return Model(declarator.SyntaxTree).GetOperation(initializer) is IVariableInitializerOperation initial
                        ? Value(initial.Value, method, bindings, depth)
                        : null;

                case IFieldReferenceOperation or IPropertyReferenceOperation { Arguments.Length: 0 }:
                    var member = (IMemberReferenceOperation)operation;
                    return member.Instance != null && !member.Member.IsStatic && KeepsBuiltValue(member.Member) &&
                           Value(member.Instance, method, bindings, depth) is { IsObject: true } built &&
                           built.Members!.TryGetValue(member.Member.OriginalDefinition, out var held)
                        ? held
                        : null;

                case IObjectCreationOperation creation:
                    return Built(creation, method, bindings, depth);

                case IInvocationOperation invocation:
                    return Returned(invocation, method, bindings, depth);

                // `bool isReviewer = role == RoleTypeId.Reviewer;` decided as a condition is — only
                // the shapes Test takes apart itself, or the two would hand a node back and forth.
                case IUnaryOperation { OperatorKind: UnaryOperatorKind.Not, OperatorMethod: null }:
                case IBinaryOperation binary when binary.OperatorMethod == null || binary.OperatorMethod.ContainingType.SpecialType == SpecialType.System_String:
                case IIsPatternOperation:
                    if (operation.Type?.SpecialType != SpecialType.System_Boolean)
                        return null;
                    var truth = Test(operation, method, bindings, depth);
                    return truth == Truth.Unknown ? null : Known.Of(truth == Truth.True);

                default:
                    return null;
            }
        }

        /// <summary>
        /// Whether a member holds what it was built with: a field, or an auto-property, that no code
        /// writes but its constructors and object initializers. Not a property an override may
        /// answer differently, or one of an interface, whose implementation decides.
        /// </summary>
        bool KeepsBuiltValue(ISymbol member)
        {
            if (_written.Contains(member.Name) || !member.Locations.Any(l => l.IsInSource))
                return false;
            if (member is IFieldSymbol)
                return true;
            if (member is not IPropertySymbol property || property.IsVirtual || property.IsAbstract || property.IsOverride || property.IsExtern ||
                property.ContainingType.TypeKind == TypeKind.Interface)
                return false;
            return property.DeclaringSyntaxReferences.All(r => r.GetSyntax() switch
            {
                PropertyDeclarationSyntax { ExpressionBody: null, AccessorList: { } accessors } =>
                    accessors.Accessors.All(a => a.Body == null && a.ExpressionBody == null),
                ParameterSyntax => true,
                _ => false
            });
        }

        /// <summary>`new T(…) { … }`: an object, never null, with the members its constructor and initializer give known values.</summary>
        Known? Built(IObjectCreationOperation creation, IMethodSymbol method, Bindings bindings, int depth)
        {
            if (creation.Type == null)
                return null;
            var members = new Dictionary<ISymbol, Known>(SymbolEqualityComparer.Default);
            if (creation.Constructor is { } constructor && depth < _depth)
                Construct(constructor.OriginalDefinition, Arguments(creation.Arguments, method, bindings, depth), depth + 1, members);

            if (creation.Initializer != null)
            {
                foreach (var initializer in creation.Initializer.Initializers)
                {
                    if (initializer is ISimpleAssignmentOperation { Target: IFieldReferenceOperation or IPropertyReferenceOperation } assignment)
                    {
                        var target = ((IMemberReferenceOperation)assignment.Target).Member.OriginalDefinition;
                        if (Value(assignment.Value, method, bindings, depth) is { } value)
                            members[target] = value;
                        else
                            members.Remove(target);
                    }
                    else if (initializer is IMemberInitializerOperation { InitializedMember: IMemberReferenceOperation nested })
                    {
                        members.Remove(nested.Member.OriginalDefinition);
                    }
                }
            }

            return Known.Object(creation.Type, members);
        }

        /// <summary>
        /// The members <paramref name="constructor"/> sets from what it is given: a `: this(…)` or
        /// `: base(…)` first, then each top-level `X = …;` of its body, up to the first statement
        /// that might skip what follows (an if that only throws does not). A member assigned
        /// anywhere else in it, or twice, is not known. A record's positional parameters set
        /// the properties of their name.
        /// </summary>
        void Construct(IMethodSymbol constructor, Bindings bindings, int depth, Dictionary<ISymbol, Known> members)
        {
            var syntax = constructor.DeclaringSyntaxReferences.FirstOrDefault()?.GetSyntax();
            if (syntax is RecordDeclarationSyntax or ParameterListSyntax)
            {
                foreach (var parameter in constructor.Parameters)
                {
                    var property = constructor.ContainingType.GetMembers(parameter.Name).OfType<IPropertySymbol>()
                        .FirstOrDefault(p => p.DeclaringSyntaxReferences.Any(r => r.GetSyntax() is ParameterSyntax));
                    if (property != null && bindings.Of(parameter.Ordinal) is { } value)
                        members[property.OriginalDefinition] = value;
                }
                return;
            }

            if (syntax is not ConstructorDeclarationSyntax declaration || Model(declaration.SyntaxTree).GetOperation(declaration) is not IConstructorBodyOperation body)
                return;

            var initializer = body.Initializer is IExpressionStatementOperation statement ? statement.Operation : body.Initializer;
            if (initializer is IInvocationOperation { TargetMethod: { MethodKind: MethodKind.Constructor } chained } chain && depth < _depth)
                Construct(chained.OriginalDefinition, Arguments(chain.Arguments, constructor, bindings, depth), depth + 1, members);

            var counts = new Dictionary<string, int>(StringComparer.Ordinal);
            foreach (var name in WrittenNames(declaration, members: true))
                counts[name] = counts.TryGetValue(name, out var count) ? count + 1 : 1;

            var settled = new HashSet<string>(StringComparer.Ordinal);
            foreach (var operation in (body.BlockBody ?? body.ExpressionBody)?.Operations ?? Enumerable.Empty<IOperation>())
            {
                if (operation is IExpressionStatementOperation
                    {
                        Operation: ISimpleAssignmentOperation { Target: IMemberReferenceOperation { Instance: IInstanceReferenceOperation } target } assignment
                    } && target is IFieldReferenceOperation or IPropertyReferenceOperation)
                {
                    var member = target.Member.OriginalDefinition;
                    if (counts.TryGetValue(member.Name, out var count) && count == 1 && Value(assignment.Value, constructor, bindings, depth) is { } value)
                    {
                        members[member] = value;
                        settled.Add(member.Name);
                    }
                    continue;
                }

                if (operation is IExpressionStatementOperation or IVariableDeclarationGroupOperation ||
                    operation is IConditionalOperation { WhenFalse: null } check && Throws(check.WhenTrue))
                    continue;
                break;
            }

            foreach (var name in counts.Keys.Where(n => !settled.Contains(n)))
                foreach (var member in members.Keys.Where(m => m.Name == name).ToList())
                    members.Remove(member);
        }

        static bool Throws(IOperation operation) => operation switch
        {
            IThrowOperation => true,
            IExpressionStatementOperation { Operation: IThrowOperation } => true,
            IBlockOperation { Operations.Length: > 0 } block => Throws(block.Operations[block.Operations.Length - 1]),
            _ => false
        };

        /// <summary>
        /// What a call to a project method returns: the value every `return` of every
        /// implementation the call may land in gives (objects: the members they agree on).
        /// </summary>
        Known? Returned(IInvocationOperation invocation, IMethodSymbol method, Bindings bindings, int depth)
        {
            if (depth >= _depth)
                return null;
            var target = invocation.TargetMethod.OriginalDefinition;
            var implementations = _context.Dispatch(new[] { target }).Distinct<IMethodSymbol>(SymbolEqualityComparer.Default).ToList();
            if (implementations.Count == 0)
                return null;

            var arguments = Arguments(invocation.Arguments, method, bindings, depth);
            Known? result = null;
            foreach (var implementation in implementations)
            {
                var value = ReturnValue(implementation, arguments, depth + 1);
                if (value == null)
                    return null;
                result = result == null ? value : Known.Join(result, value);
                if (result == null)
                    return null;
            }
            return result;
        }

        Known? ReturnValue(IMethodSymbol method, Bindings bindings, int depth)
        {
            var key = (method, bindings.Key, depth);
            if (_returned.TryGetValue(key, out var known))
                return known;
            _returned[key] = null;   // a recursion finds nothing

            known = null;
            var syntax = method.DeclaringSyntaxReferences.FirstOrDefault()?.GetSyntax();
            if (syntax is MethodDeclarationSyntax declaration && !method.IsAsync && !_context.IsGenerated(declaration.SyntaxTree) &&
                Model(declaration.SyntaxTree).GetOperation(declaration) is IMethodBodyOperation body)
            {
                var returns = ((IOperation?)body.BlockBody ?? body.ExpressionBody)?.Descendants().OfType<IReturnOperation>()
                    .Where(r => r.Syntax.Ancestors().FirstOrDefault(n => n is AnonymousFunctionExpressionSyntax or LocalFunctionStatementSyntax or MemberDeclarationSyntax) == declaration)
                    .ToList();
                if (returns != null && returns.Count > 0 && returns.All(r => r.Kind == OperationKind.Return && r.ReturnedValue != null))
                {
                    foreach (var returned in returns)
                    {
                        var value = Value(returned.ReturnedValue, method, bindings, depth);
                        known = value == null ? null : known == null ? value : Known.Join(known, value);
                        if (known == null)
                            break;
                    }
                }
            }

            _returned[key] = known;
            return known;
        }

        /// <summary>The values a call's arguments have, by the ordinal of the parameter each binds to.</summary>
        Bindings Arguments(IEnumerable<IArgumentOperation> arguments, IMethodSymbol method, Bindings bindings, int depth)
        {
            var values = new SortedDictionary<int, Known>();
            foreach (var argument in arguments)
                if (argument.Parameter is { RefKind: RefKind.None or RefKind.In } parameter && argument.ArgumentKind != ArgumentKind.ParamArray &&
                    Value(argument.Value, method, bindings, depth) is { } value)
                    values[parameter.Ordinal] = value;
            return values.Count == 0 ? Bindings.None : new Bindings(values);
        }

        // -- syntax ------------------------------------------------------------------------------

        SemanticModel Model(SyntaxTree tree)
        {
            if (!_models.TryGetValue(tree, out var model))
                _models[tree] = model = _context.Full.GetSemanticModel(tree);
            return model;
        }

        /// <summary>The names a method assigns as variables anywhere in its body: its parameters and locals that are not what they started as.</summary>
        HashSet<string> Assigned(IMethodSymbol method)
        {
            if (!_assigned.TryGetValue(method, out var names))
            {
                names = new HashSet<string>(method.DeclaringSyntaxReferences.SelectMany(r => WrittenNames(r.GetSyntax(), members: false)), StringComparer.Ordinal);
                _assigned[method] = names;
            }
            return names;
        }

        /// <summary>
        /// The names written in <paramref name="root"/>: assigned (tuples included), incremented,
        /// passed by ref or out. Plain names only, or with <paramref name="members"/> also
        /// `this.X` — what a constructor sets.
        /// </summary>
        static IEnumerable<string> WrittenNames(SyntaxNode root, bool members)
        {
            foreach (var node in root.DescendantNodes())
            {
                var target = node switch
                {
                    AssignmentExpressionSyntax assignment => assignment.Left,
                    PrefixUnaryExpressionSyntax prefix when prefix.IsKind(SyntaxKind.PreIncrementExpression) || prefix.IsKind(SyntaxKind.PreDecrementExpression) => prefix.Operand,
                    PostfixUnaryExpressionSyntax postfix when postfix.IsKind(SyntaxKind.PostIncrementExpression) || postfix.IsKind(SyntaxKind.PostDecrementExpression) => postfix.Operand,
                    ArgumentSyntax argument when argument.RefKindKeyword.IsKind(SyntaxKind.RefKeyword) || argument.RefKindKeyword.IsKind(SyntaxKind.OutKeyword) => argument.Expression,
                    RefExpressionSyntax reference => reference.Expression,   // `ref var alias = ref kind;` may write it later
                    _ => null
                };
                if (target == null)
                    continue;
                foreach (var name in Names(target))
                    yield return name;
            }

            IEnumerable<string> Names(ExpressionSyntax target) => target switch
            {
                ParenthesizedExpressionSyntax parenthesized => Names(parenthesized.Expression),
                TupleExpressionSyntax tuple => tuple.Arguments.SelectMany(a => Names(a.Expression)),
                IdentifierNameSyntax identifier => new[] { identifier.Identifier.ValueText },
                MemberAccessExpressionSyntax { Expression: ThisExpressionSyntax } member when members => new[] { member.Name.Identifier.ValueText },
                _ => Array.Empty<string>()
            };
        }

        sealed class NodeAndMethod : IEqualityComparer<(SyntaxNode Node, IMethodSymbol Method)>
        {
            public bool Equals((SyntaxNode Node, IMethodSymbol Method) x, (SyntaxNode Node, IMethodSymbol Method) y) =>
                x.Node == y.Node && SymbolEqualityComparer.Default.Equals(x.Method, y.Method);

            public int GetHashCode((SyntaxNode Node, IMethodSymbol Method) obj) => HashCode.Combine(obj.Node, SymbolEqualityComparer.Default.GetHashCode(obj.Method));
        }

        sealed class CallAndMethod : IEqualityComparer<(CallSite Call, IMethodSymbol Method, string Key)>
        {
            public bool Equals((CallSite Call, IMethodSymbol Method, string Key) x, (CallSite Call, IMethodSymbol Method, string Key) y) =>
                x.Call == y.Call && x.Key == y.Key && SymbolEqualityComparer.Default.Equals(x.Method, y.Method);

            public int GetHashCode((CallSite Call, IMethodSymbol Method, string Key) obj) =>
                HashCode.Combine(obj.Call, obj.Key, SymbolEqualityComparer.Default.GetHashCode(obj.Method));
        }

        sealed class MethodBindingsDepth : IEqualityComparer<(IMethodSymbol Method, string Key, int Depth)>
        {
            public bool Equals((IMethodSymbol Method, string Key, int Depth) x, (IMethodSymbol Method, string Key, int Depth) y) =>
                x.Depth == y.Depth && x.Key == y.Key && SymbolEqualityComparer.Default.Equals(x.Method, y.Method);

            public int GetHashCode((IMethodSymbol Method, string Key, int Depth) obj) =>
                HashCode.Combine(obj.Depth, obj.Key, SymbolEqualityComparer.Default.GetHashCode(obj.Method));
        }
    }
}
