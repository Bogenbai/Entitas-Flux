using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Entitas.Graph.Analysis
{
    /// <summary>
    /// The conditions a private method runs under, from the calls its own type makes to it.
    /// CommitCartByRequestSystem.Execute calls PayForCart only when the cart has items and can be
    /// paid for, and so every write in PayForCart happens only then; but a condition is read
    /// within one method (TreeWalker.ConditionOf), and Propagate follows only calls into other
    /// types, so those writes read as unconditional.
    ///
    /// A condition shown must hold, so this is sound before it is complete:
    /// - only private ordinary methods without attributes. Anything else can be called from where
    ///   the call graph does not look: other types, an interface, a Unity message, an [Inject], an
    ///   event wired in the inspector.
    /// - only when every call is in the type, is recorded, and is conditional. One unconditional
    ///   call, a call from a field initializer, or the name used other than in a call (a method
    ///   group handed to an event, nameof) — and the method runs whenever its type's code does.
    /// - calls under the two sides of one branch (an if and its else) cancel, as sites do
    ///   (<see cref="Guards.Reduce"/>).
    /// - a caller's own conditions are added for up to <see cref="Depth"/> private calls up. Past
    ///   that, or around a recursion, the call's condition alone stands: weaker, and still true.
    /// </summary>
    sealed class CallerGuards
    {
        const int Depth = 3;

        readonly Dictionary<IMethodSymbol, List<CallSite>> _calls;
        readonly HashSet<IMethodSymbol> _calledOutsideMethods;
        readonly Dictionary<IMethodSymbol, IReadOnlyList<Condition>?> _known = new Dictionary<IMethodSymbol, IReadOnlyList<Condition>?>(SymbolEqualityComparer.Default);
        readonly Dictionary<INamedTypeSymbol, HashSet<string>> _otherUses = new Dictionary<INamedTypeSymbol, HashSet<string>>(SymbolEqualityComparer.Default);
        readonly Dictionary<INamedTypeSymbol, bool> _unityObjects = new Dictionary<INamedTypeSymbol, bool>(SymbolEqualityComparer.Default);

        public CallerGuards(IEnumerable<CallSite> calls, IEnumerable<IMethodSymbol> calledOutsideMethods)
        {
            _calls = calls
                .GroupBy<CallSite, IMethodSymbol>(c => c.Callee, SymbolEqualityComparer.Default)
                .ToDictionary<IGrouping<IMethodSymbol, CallSite>, IMethodSymbol, List<CallSite>>(g => g.Key, g => g.ToList(), SymbolEqualityComparer.Default);
            _calledOutsideMethods = new HashSet<IMethodSymbol>(calledOutsideMethods, SymbolEqualityComparer.Default);
        }

        /// <summary>The ways <paramref name="method"/> is called within its type, when they are all conditional; null when it may run unconditionally.</summary>
        public IReadOnlyList<Condition>? Of(IMethodSymbol? method)
        {
            if (method == null)
                return null;
            if (!_known.TryGetValue(method, out var guard))
                _known[method] = guard = Of(method, new HashSet<IMethodSymbol>(SymbolEqualityComparer.Default));
            return guard;
        }

        /// <param name="path">The methods whose callers are being asked for: their number is the depth, and one met again is a recursion.</param>
        IReadOnlyList<Condition>? Of(IMethodSymbol method, HashSet<IMethodSymbol> path)
        {
            if (path.Count >= Depth || path.Contains(method) || !MayTakeCallerConditions(method) ||
                !_calls.TryGetValue(method, out var sites))
                return null;

            path.Add(method);
            try
            {
                var ways = new List<Condition>();
                foreach (var site in sites)
                {
                    if (!SymbolEqualityComparer.Default.Equals(site.Caller.ContainingType, method.ContainingType))
                        return null;
                    var way = Guards.And(Of(site.Caller, path), site.Condition);
                    if (way == null)
                        return null;
                    ways.AddRange(way);
                }

                return UsedOtherThanCalled(method) ? null : Guards.Reduce(ways);
            }
            finally
            {
                path.Remove(method);
            }
        }

        bool MayTakeCallerConditions(IMethodSymbol method) =>
            method.MethodKind == MethodKind.Ordinary &&
            method.DeclaredAccessibility == Accessibility.Private &&
            method.PartialDefinitionPart == null && method.PartialImplementationPart == null &&
            method.GetAttributes().Length == 0 &&
            !_calledOutsideMethods.Contains(method) &&
            !(IsUnityObject(method.ContainingType) && IsUnityMessage(method.Name));

        /// <summary>
        /// Unity calls a MonoBehaviour's private Update, OnEnable or OnTriggerEnter by name; none of
        /// those calls is in the code.
        /// </summary>
        static bool IsUnityMessage(string name) =>
            name is "Awake" or "Start" or "Update" or "FixedUpdate" or "LateUpdate" or "Reset" ||
            name.StartsWith("On", StringComparison.Ordinal);

        bool IsUnityObject(INamedTypeSymbol type)
        {
            if (!_unityObjects.TryGetValue(type, out var known))
            {
                known = false;
                for (var current = type.BaseType; current != null && !known; current = current.BaseType)
                    known = current.Name == "Object" && current.ContainingNamespace?.ToDisplayString() == "UnityEngine";
                _unityObjects[type] = known;
            }
            return known;
        }

        /// <summary>
        /// Whether the method's name appears in its type other than as what a call invokes: a
        /// method group (`OnAdded += Pay`), a delegate, nameof. Such a use runs it from wherever the
        /// delegate goes. By name, not symbol — a field of the same name makes this answer yes,
        /// which only costs a condition — and asked only of methods whose calls are all conditional.
        /// </summary>
        bool UsedOtherThanCalled(IMethodSymbol method)
        {
            var type = method.ContainingType;
            if (!_otherUses.TryGetValue(type, out var names))
            {
                names = new HashSet<string>(StringComparer.Ordinal);
                foreach (var reference in type.DeclaringSyntaxReferences)
                {
                    foreach (var name in reference.GetSyntax().DescendantNodes().OfType<SimpleNameSyntax>())
                    {
                        SyntaxNode target = name.Parent is MemberAccessExpressionSyntax access && access.Name == name ? access : name;
                        if (!(target.Parent is InvocationExpressionSyntax call && call.Expression == target))
                            names.Add(name.Identifier.ValueText);
                    }
                }
                _otherUses[type] = names;
            }
            return names.Contains(method.Name);
        }
    }
}
