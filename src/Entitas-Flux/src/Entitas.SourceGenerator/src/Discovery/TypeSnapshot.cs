using System;
using System.Collections.Generic;
using System.Linq;
using Entitas.SourceGenerator.CodeGeneration;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Entitas.SourceGenerator.Discovery
{
    /// <summary>
    /// Everything discovery needs to know about ONE type, flattened into values.
    ///
    /// Discovery used to read <see cref="INamedTypeSymbol"/>s directly, which forced the
    /// whole assembly to be walked on every keystroke. Snapshots are taken per type
    /// declaration in the syntax pipeline instead, so Roslyn only re-takes them for
    /// files that actually changed, and compares them by value to decide whether
    /// anything downstream must re-run.
    ///
    /// Consequently a snapshot must hold no symbols and no syntax — only strings, bools
    /// and arrays of those.
    /// </summary>
    public sealed class TypeSnapshot : IEquatable<TypeSnapshot>
    {
        public string FullName { get; }
        public bool IsAbstract { get; }
        public bool IsGenericType { get; }
        public bool IsComponent { get; }
        public AttributeSnapshot[] Attributes { get; }
        public MemberSnapshot[] Members { get; }
        public MethodSnapshot[] Methods { get; }

        public TypeSnapshot(
            string fullName,
            bool isAbstract,
            bool isGenericType,
            bool isComponent,
            AttributeSnapshot[] attributes,
            MemberSnapshot[] members,
            MethodSnapshot[] methods)
        {
            FullName = fullName;
            IsAbstract = isAbstract;
            IsGenericType = isGenericType;
            IsComponent = isComponent;
            Attributes = attributes;
            Members = members;
            Methods = methods;
        }

        public static TypeSnapshot From(INamedTypeSymbol type) => new TypeSnapshot(
            type.ToCompilableString(),
            type.IsAbstract,
            type.IsGenericType,
            type.AllInterfaces.Any(i => i.ToCompilableString() == WellKnownTypes.ComponentInterface),
            type.GetAttributes().Select(AttributeSnapshot.From).ToArray(),
            type.GetPublicMembers(true)
                .Select(member => new MemberSnapshot(
                    member.Name,
                    member.PublicMemberType().ToCompilableString(),
                    member.GetAttributes().Select(AttributeSnapshot.From).ToArray()))
                .ToArray(),
            // Only attributed methods can matter ([EntityIndexGetMethod]); skipping the
            // rest keeps snapshots small on types with large public surfaces.
            type.GetMembers()
                .OfType<IMethodSymbol>()
                .Where(method => method.DeclaredAccessibility == Accessibility.Public)
                .Where(method => !method.IsStatic)
                .Where(method => method.GetAttributes().Length > 0)
                .Select(method => new MethodSnapshot(
                    method.Name,
                    method.ReturnType.ToCompilableString(),
                    method.Parameters
                        .Select(p => new MemberSnapshot(p.Name, p.Type.ToCompilableString(), new AttributeSnapshot[0]))
                        .ToArray(),
                    method.GetAttributes().Select(AttributeSnapshot.From).ToArray()))
                .ToArray());

        public AttributeSnapshot? GetAttribute(string attributeFullName, bool inherit = false) =>
            Attributes.FirstOrDefault(attribute => attribute.Matches(attributeFullName, inherit));

        public AttributeSnapshot[] GetAttributes(string attributeFullName) =>
            Attributes.Where(attribute => attribute.Matches(attributeFullName, false)).ToArray();

        public bool Equals(TypeSnapshot? other) =>
            other != null &&
            string.Equals(FullName, other.FullName, StringComparison.Ordinal) &&
            IsAbstract == other.IsAbstract &&
            IsGenericType == other.IsGenericType &&
            IsComponent == other.IsComponent &&
            SnapshotEquality.ArrayEquals(Attributes, other.Attributes) &&
            SnapshotEquality.ArrayEquals(Members, other.Members) &&
            SnapshotEquality.ArrayEquals(Methods, other.Methods);

        public override bool Equals(object? obj) => Equals(obj as TypeSnapshot);

        public override int GetHashCode() => unchecked(
            StringComparer.Ordinal.GetHashCode(FullName) * 397 ^ (Attributes.Length * 31 + Members.Length));
    }

    public sealed class MemberSnapshot : IEquatable<MemberSnapshot>
    {
        public string Name { get; }
        public string TypeName { get; }
        public AttributeSnapshot[] Attributes { get; }

        public MemberSnapshot(string name, string typeName, AttributeSnapshot[] attributes)
        {
            Name = name;
            TypeName = typeName;
            Attributes = attributes;
        }

        public AttributeSnapshot? GetAttribute(string attributeFullName, bool inherit = false) =>
            Attributes.FirstOrDefault(attribute => attribute.Matches(attributeFullName, inherit));

        public bool Equals(MemberSnapshot? other) =>
            other != null &&
            string.Equals(Name, other.Name, StringComparison.Ordinal) &&
            string.Equals(TypeName, other.TypeName, StringComparison.Ordinal) &&
            SnapshotEquality.ArrayEquals(Attributes, other.Attributes);

        public override bool Equals(object? obj) => Equals(obj as MemberSnapshot);

        public override int GetHashCode() => unchecked(
            StringComparer.Ordinal.GetHashCode(Name) * 397 ^ StringComparer.Ordinal.GetHashCode(TypeName));
    }

    public sealed class MethodSnapshot : IEquatable<MethodSnapshot>
    {
        public string Name { get; }
        public string ReturnTypeName { get; }
        public MemberSnapshot[] Parameters { get; }
        public AttributeSnapshot[] Attributes { get; }

        public MethodSnapshot(string name, string returnTypeName, MemberSnapshot[] parameters, AttributeSnapshot[] attributes)
        {
            Name = name;
            ReturnTypeName = returnTypeName;
            Parameters = parameters;
            Attributes = attributes;
        }

        public AttributeSnapshot? GetAttribute(string attributeFullName, bool inherit = false) =>
            Attributes.FirstOrDefault(attribute => attribute.Matches(attributeFullName, inherit));

        public bool Equals(MethodSnapshot? other) =>
            other != null &&
            string.Equals(Name, other.Name, StringComparison.Ordinal) &&
            string.Equals(ReturnTypeName, other.ReturnTypeName, StringComparison.Ordinal) &&
            SnapshotEquality.ArrayEquals(Parameters, other.Parameters) &&
            SnapshotEquality.ArrayEquals(Attributes, other.Attributes);

        public override bool Equals(object? obj) => Equals(obj as MethodSnapshot);

        public override int GetHashCode() => unchecked(
            StringComparer.Ordinal.GetHashCode(Name) * 397 ^ Parameters.Length);
    }

    /// <summary>
    /// One attribute application. <see cref="TypeNames"/> is the attribute class plus its
    /// base chain, which is what makes inherited lookups (e.g. [EntityIndex] deriving from
    /// AbstractEntityIndexAttribute) work without symbols.
    /// </summary>
    public sealed class AttributeSnapshot : IEquatable<AttributeSnapshot>
    {
        public string[] TypeNames { get; }
        public string SimpleName { get; }

        /// <summary>False only for unresolved attributes — the "possible compiler error" case.</summary>
        public bool HasBaseType { get; }

        /// <summary>Constructor arguments as strings; params arrays are flattened, typeof() renders as the type name.</summary>
        public string?[] Arguments { get; }

        /// <summary>
        /// For a generated context attribute ([Game] deriving from ContextAttribute), the
        /// literal its constructor passes to base(...) — read from syntax while a symbol
        /// is still available.
        /// </summary>
        public string? ContextLiteral { get; }

        public AttributeSnapshot(string[] typeNames, string simpleName, bool hasBaseType, string?[] arguments, string? contextLiteral)
        {
            TypeNames = typeNames;
            SimpleName = simpleName;
            HasBaseType = hasBaseType;
            Arguments = arguments;
            ContextLiteral = contextLiteral;
        }

        public string FullName => TypeNames.Length > 0 ? TypeNames[0] : string.Empty;

        public string? BaseTypeName => TypeNames.Length > 1 ? TypeNames[1] : null;

        public bool Matches(string attributeFullName, bool inherit)
        {
            if (TypeNames.Length == 0)
                return false;

            return inherit
                ? TypeNames.Any(name => name == attributeFullName)
                : TypeNames[0] == attributeFullName;
        }

        public static AttributeSnapshot From(AttributeData attribute)
        {
            var attributeClass = attribute.AttributeClass;

            var typeNames = new List<string>();
            for (var current = attributeClass; current != null; current = current.BaseType)
                typeNames.Add(current.ToCompilableString());

            var arguments = attribute.ConstructorArguments
                .SelectMany(argument => argument.Kind == TypedConstantKind.Array
                    ? argument.Values.Select(Render)
                    : new[] { Render(argument) })
                .ToArray();

            return new AttributeSnapshot(
                typeNames.ToArray(),
                attributeClass?.Name ?? string.Empty,
                attributeClass?.BaseType != null,
                arguments,
                TryReadContextLiteral(attribute));
        }

        static string? Render(TypedConstant argument) => argument.Value switch
        {
            null => null,
            ITypeSymbol type => type.ToCompilableString(),
            string text => text,
            bool flag => flag ? "true" : "false",
            var value => Convert.ToString(value, System.Globalization.CultureInfo.InvariantCulture)
        };

        /// <summary>
        /// Generated context attributes carry their context name only in the literal they
        /// pass to base(...), so it is read from the constructor's syntax.
        /// </summary>
        static string? TryReadContextLiteral(AttributeData attribute)
        {
            var syntaxRef = attribute.AttributeConstructor?.DeclaringSyntaxReferences.FirstOrDefault();
            if (syntaxRef == null)
                return null;

            var baseInitializer = syntaxRef.GetSyntax()
                .DescendantNodes()
                .FirstOrDefault(node => node.IsKind(SyntaxKind.BaseConstructorInitializer)) as ConstructorInitializerSyntax;

            var argument = baseInitializer?.ArgumentList.Arguments.FirstOrDefault()?.Expression as LiteralExpressionSyntax;
            return argument?.ToString().Replace("\"", string.Empty);
        }

        public bool Equals(AttributeSnapshot? other) =>
            other != null &&
            SnapshotEquality.StringsEqual(TypeNames, other.TypeNames) &&
            string.Equals(SimpleName, other.SimpleName, StringComparison.Ordinal) &&
            HasBaseType == other.HasBaseType &&
            SnapshotEquality.StringsEqual(Arguments, other.Arguments) &&
            string.Equals(ContextLiteral, other.ContextLiteral, StringComparison.Ordinal);

        public override bool Equals(object? obj) => Equals(obj as AttributeSnapshot);

        public override int GetHashCode() => unchecked(
            StringComparer.Ordinal.GetHashCode(SimpleName) * 397 ^ Arguments.Length);
    }

    static class SnapshotEquality
    {
        public static bool ArrayEquals<T>(T[] left, T[] right) where T : IEquatable<T>
        {
            if (ReferenceEquals(left, right))
                return true;
            if (left.Length != right.Length)
                return false;

            for (var i = 0; i < left.Length; i++)
            {
                if (!left[i].Equals(right[i]))
                    return false;
            }

            return true;
        }

        public static bool StringsEqual(string?[] left, string?[] right)
        {
            if (ReferenceEquals(left, right))
                return true;
            if (left.Length != right.Length)
                return false;

            for (var i = 0; i < left.Length; i++)
            {
                if (!string.Equals(left[i], right[i], StringComparison.Ordinal))
                    return false;
            }

            return true;
        }
    }
}
