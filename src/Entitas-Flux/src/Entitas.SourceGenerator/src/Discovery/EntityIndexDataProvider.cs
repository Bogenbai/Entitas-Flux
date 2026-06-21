using System;
using System.Linq;
using Entitas.SourceGenerator.CodeGeneration;
using Microsoft.CodeAnalysis;

namespace Entitas.SourceGenerator.Discovery
{
    /// <summary>
    /// Ported from the Roslyn EntityIndexDataProvider. Produces member-based entity
    /// indices ([EntityIndex]/[PrimaryEntityIndex] on component members) and custom
    /// entity indices ([CustomEntityIndex] on a type with [EntityIndexGetMethod]).
    /// </summary>
    public sealed class EntityIndexDataProvider
    {
        readonly INamedTypeSymbol[] _types;
        readonly ContextResolver _contextResolver;
        readonly bool _ignoreNamespaces;

        public EntityIndexDataProvider(INamedTypeSymbol[] types, ContextResolver contextResolver, bool ignoreNamespaces = false)
        {
            _types = types;
            _contextResolver = contextResolver;
            _ignoreNamespaces = ignoreNamespaces;
        }

        public EntityIndexData[] GetData()
        {
            var componentInterface = WellKnownTypes.ComponentInterface;

            var entityIndexData = _types
                .Where(type => type.AllInterfaces.Any(i => i.ToCompilableString() == componentInterface))
                .Where(type => !type.IsAbstract)
                .Where(type => type.GetAttribute(AttributeNames.DontGenerate) == null)
                .Select(type => (type, members: type.GetPublicMembers(true)))
                .Where(kv => kv.members.Any(symbol => symbol.GetAttribute(AttributeNames.AbstractEntityIndex, true) != null))
                .SelectMany(kv => CreateEntityIndexData(kv.type, kv.members));

            var customEntityIndexData = _types
                .Where(type => !type.IsAbstract)
                .Where(type => type.GetAttribute(AttributeNames.CustomEntityIndex) != null)
                .Select(CreateCustomEntityIndexData);

            return entityIndexData
                .Concat(customEntityIndexData)
                .ToArray();
        }

        EntityIndexData[] CreateEntityIndexData(INamedTypeSymbol type, ISymbol[] members)
        {
            var hasMultiple = members.Count(member => member.GetAttribute(AttributeNames.AbstractEntityIndex, true) != null) > 1;
            return members
                .Where(member => member.GetAttribute(AttributeNames.AbstractEntityIndex, true) != null)
                .Select(member =>
                {
                    var data = new EntityIndexData();
                    var attribute = member.GetAttribute(AttributeNames.AbstractEntityIndex, true)!;

                    data.SetEntityIndexType(GetEntityIndexType(attribute));
                    data.IsCustom(false);
                    data.SetEntityIndexName(type.ToCompilableString().ToComponentName(_ignoreNamespaces));
                    data.SetHasMultiple(hasMultiple);
                    data.SetKeyType(member.PublicMemberType().ToCompilableString());
                    data.SetComponentType(type.ToCompilableString());
                    data.SetMemberName(member.Name);
                    data.SetContextNames(_contextResolver.GetContextNamesOrDefault(type));

                    return data;
                }).ToArray();
        }

        EntityIndexData CreateCustomEntityIndexData(INamedTypeSymbol type)
        {
            var data = new EntityIndexData();
            var attribute = type.GetAttribute(AttributeNames.CustomEntityIndex)!;
            data.SetEntityIndexType(type.ToCompilableString());
            data.IsCustom(true);
            data.SetEntityIndexName(type.ToCompilableString().RemoveDots());
            data.SetHasMultiple(false);
            data.SetContextNames(new[]
            {
                ((INamedTypeSymbol)attribute.ConstructorArguments.First().Value!)
                .ToCompilableString()
                .TypeName()
                .RemoveContextSuffix()
            });

            var getMethods = type
                .GetMembers()
                .OfType<IMethodSymbol>()
                .Where(method => method.DeclaredAccessibility == Accessibility.Public)
                .Where(method => !method.IsStatic)
                .Where(method => method.GetAttribute(AttributeNames.EntityIndexGetMethod) != null)
                .Select(method => new MethodData(
                    method.ReturnType.ToCompilableString(),
                    method.Name,
                    method.Parameters
                        .Select(p => new MemberData(p.Type.ToCompilableString(), p.Name))
                        .ToArray()
                ))
                .ToArray();

            data.SetCustomMethods(getMethods);

            return data;
        }

        static string GetEntityIndexType(AttributeData attribute)
        {
            var entityIndexType = attribute.AttributeClass?.ToCompilableString();
            switch (entityIndexType)
            {
                case AttributeNames.EntityIndex:
                    return "Entitas.EntityIndex";
                case AttributeNames.PrimaryEntityIndex:
                    return "Entitas.PrimaryEntityIndex";
                default:
                    throw new Exception($"Unhandled EntityIndexType: {entityIndexType}");
            }
        }
    }
}
