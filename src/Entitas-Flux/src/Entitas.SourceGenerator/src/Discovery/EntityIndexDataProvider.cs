using System;
using System.Linq;
using Entitas.SourceGenerator.CodeGeneration;

namespace Entitas.SourceGenerator.Discovery
{
    /// <summary>
    /// Ported from the Roslyn EntityIndexDataProvider. Produces member-based entity
    /// indices ([EntityIndex]/[PrimaryEntityIndex] on component members) and custom
    /// entity indices ([CustomEntityIndex] on a type with [EntityIndexGetMethod]).
    /// </summary>
    public sealed class EntityIndexDataProvider
    {
        readonly TypeSnapshot[] _types;
        readonly ContextResolver _contextResolver;
        readonly bool _ignoreNamespaces;

        public EntityIndexDataProvider(TypeSnapshot[] types, ContextResolver contextResolver, bool ignoreNamespaces = false)
        {
            _types = types;
            _contextResolver = contextResolver;
            _ignoreNamespaces = ignoreNamespaces;
        }

        public EntityIndexData[] GetData()
        {
            var entityIndexData = _types
                .Where(type => type.IsComponent)
                .Where(type => !type.IsAbstract)
                .Where(type => type.GetAttribute(AttributeNames.DontGenerate) == null)
                .Select(type => (type, members: type.Members))
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

        EntityIndexData[] CreateEntityIndexData(TypeSnapshot type, MemberSnapshot[] members)
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
                    data.SetEntityIndexName(type.FullName.ToComponentName(_ignoreNamespaces));
                    data.SetHasMultiple(hasMultiple);
                    data.SetKeyType(member.TypeName);
                    data.SetComponentType(type.FullName);
                    data.SetMemberName(member.Name);
                    data.SetContextNames(_contextResolver.GetContextNamesOrDefault(type));

                    return data;
                }).ToArray();
        }

        EntityIndexData CreateCustomEntityIndexData(TypeSnapshot type)
        {
            var data = new EntityIndexData();
            var attribute = type.GetAttribute(AttributeNames.CustomEntityIndex)!;
            data.SetEntityIndexType(type.FullName);
            data.IsCustom(true);
            data.SetEntityIndexName(type.FullName.RemoveDots());
            data.SetHasMultiple(false);
            data.SetContextNames(new[]
            {
                attribute.Arguments.First()!
                .TypeName()
                .RemoveContextSuffix()
            });

            var getMethods = type.Methods
                .Where(method => method.GetAttribute(AttributeNames.EntityIndexGetMethod) != null)
                .Select(method => new MethodData(
                    method.ReturnTypeName,
                    method.Name,
                    method.Parameters
                        .Select(p => new MemberData(p.TypeName, p.Name))
                        .ToArray()
                ))
                .ToArray();

            data.SetCustomMethods(getMethods);

            return data;
        }

        static string GetEntityIndexType(AttributeSnapshot attribute)
        {
            var entityIndexType = attribute.FullName;
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
