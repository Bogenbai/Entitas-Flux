using System.Collections.Generic;
using System.Linq;
using Microsoft.CodeAnalysis;

namespace Entitas.SourceGenerator.CodeGeneration
{
    /// <summary>
    /// Local replacement for DesperateDevs.Roslyn symbol helpers. All attribute
    /// lookups are string-based (metadata/fully-qualified names) so we never need
    /// to reference the attribute types.
    /// </summary>
    public static class SymbolExtensions
    {
        // Reproduces the legacy DesperateDevs.Roslyn output: built-in types render
        // as C# keywords (int, not System.Int32) and other types are fully-qualified
        // WITHOUT a global:: prefix.
        static readonly SymbolDisplayFormat CompilableFormat = new SymbolDisplayFormat(
            globalNamespaceStyle: SymbolDisplayGlobalNamespaceStyle.Omitted,
            typeQualificationStyle: SymbolDisplayTypeQualificationStyle.NameAndContainingTypesAndNamespaces,
            genericsOptions: SymbolDisplayGenericsOptions.IncludeTypeParameters,
            miscellaneousOptions: SymbolDisplayMiscellaneousOptions.UseSpecialTypes);

        public static string ToCompilableString(this ISymbol symbol) =>
            symbol.ToDisplayString(CompilableFormat);

        public static AttributeData? GetAttribute(this ISymbol symbol, string attributeFullName, bool inherit = false)
        {
            return symbol.GetAttributes()
                .FirstOrDefault(attr => MatchesAttribute(attr.AttributeClass, attributeFullName, inherit));
        }

        public static AttributeData[] GetAttributes(this ISymbol symbol, string attributeFullName)
        {
            return symbol.GetAttributes()
                .Where(attr => MatchesAttribute(attr.AttributeClass, attributeFullName, false))
                .ToArray();
        }

        static bool MatchesAttribute(INamedTypeSymbol? attributeClass, string attributeFullName, bool inherit)
        {
            var current = attributeClass;
            while (current != null)
            {
                if (current.ToCompilableString() == attributeFullName)
                    return true;

                if (!inherit)
                    return false;

                current = current.BaseType;
            }

            return false;
        }

        /// <summary>
        /// Public, non-static instance fields and properties. For components we
        /// require readable+writable properties (matching the legacy semantics
        /// used by the component member/index providers).
        /// </summary>
        public static ISymbol[] GetPublicMembers(this INamedTypeSymbol type, bool isComponent)
        {
            var members = new List<ISymbol>();
            foreach (var current in GetBaseTypesAndThis(type))
            {
                foreach (var member in current.GetMembers())
                {
                    if (member.DeclaredAccessibility != Accessibility.Public)
                        continue;
                    if (member.IsStatic)
                        continue;

                    if (member is IFieldSymbol field)
                    {
                        if (field.IsConst || field.IsImplicitlyDeclared)
                            continue;
                        members.Add(field);
                    }
                    else if (member is IPropertySymbol property)
                    {
                        if (property.IsIndexer)
                            continue;
                        if (property.GetMethod == null || property.SetMethod == null)
                            continue;
                        members.Add(property);
                    }
                }
            }

            return members.ToArray();
        }

        public static ITypeSymbol PublicMemberType(this ISymbol member) => member switch
        {
            IFieldSymbol field => field.Type,
            IPropertySymbol property => property.Type,
            _ => throw new System.ArgumentException($"Unsupported member kind: {member.Kind}")
        };

        static IEnumerable<INamedTypeSymbol> GetBaseTypesAndThis(INamedTypeSymbol type)
        {
            var current = type;
            while (current != null && current.SpecialType != SpecialType.System_Object)
            {
                yield return current;
                current = current.BaseType;
            }
        }
    }
}
