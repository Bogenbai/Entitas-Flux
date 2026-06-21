using System.Linq;
using Entitas.SourceGenerator.CodeGeneration;
using Microsoft.CodeAnalysis;

namespace Entitas.SourceGenerator.Discovery
{
    public interface IComponentDataProvider
    {
        void Provide(INamedTypeSymbol type, ComponentData data);
    }

    public sealed class ComponentTypeComponentDataProvider : IComponentDataProvider
    {
        public void Provide(INamedTypeSymbol type, ComponentData data)
        {
            data.SetTypeName(type.ToCompilableString());
        }
    }

    public sealed class MemberDataComponentDataProvider : IComponentDataProvider
    {
        public void Provide(INamedTypeSymbol type, ComponentData data)
        {
            var isComponent = type.AllInterfaces.Any(i => i.ToCompilableString() == WellKnownTypes.ComponentInterface);
            var memberData = type.GetPublicMembers(isComponent)
                .Select(CreateMemberData)
                .ToArray();

            data.SetMemberData(memberData);
        }

        static MemberData CreateMemberData(ISymbol member) =>
            new MemberData(member.PublicMemberType().ToCompilableString(), member.Name);
    }

    public sealed class ContextsComponentDataProvider : IComponentDataProvider
    {
        readonly ContextResolver _resolver;

        public ContextsComponentDataProvider(ContextResolver resolver) => _resolver = resolver;

        public void Provide(INamedTypeSymbol type, ComponentData data)
        {
            data.SetContextNames(_resolver.GetContextNamesOrDefault(type));
        }
    }

    public sealed class IsUniqueComponentDataProvider : IComponentDataProvider
    {
        public void Provide(INamedTypeSymbol type, ComponentData data)
        {
            var isUnique = type.GetAttribute(AttributeNames.Unique) != null;
            data.IsUnique(isUnique);
        }
    }

    public sealed class FlagPrefixComponentDataProvider : IComponentDataProvider
    {
        public void Provide(INamedTypeSymbol type, ComponentData data)
        {
            data.SetFlagPrefix(GetCustomComponentPrefix(type));
        }

        static string GetCustomComponentPrefix(INamedTypeSymbol type)
        {
            var attr = type.GetAttribute(AttributeNames.FlagPrefix);
            return attr == null ? "is" : (string)attr.ConstructorArguments.First().Value!;
        }
    }

    public sealed class ShouldWatchChangesComponentDataProvider : IComponentDataProvider
    {
        public void Provide(INamedTypeSymbol type, ComponentData data)
        {
            var shouldTrackChanges = type.GetAttribute(AttributeNames.Watched) != null;
            data.ShouldWatchChanges(shouldTrackChanges);
        }
    }

    public sealed class ShouldGenerateComponentComponentDataProvider : IComponentDataProvider
    {
        public void Provide(INamedTypeSymbol type, ComponentData data)
        {
            var shouldGenerateComponent = !type.AllInterfaces.Any(i => i.ToCompilableString() == WellKnownTypes.ComponentInterface);
            data.ShouldGenerateComponent(shouldGenerateComponent);
            if (shouldGenerateComponent)
                data.SetObjectTypeName(type.ToCompilableString());
        }
    }

    public sealed class ShouldGenerateMethodsComponentDataProvider : IComponentDataProvider
    {
        public void Provide(INamedTypeSymbol type, ComponentData data)
        {
            var generate = type.GetAttribute(AttributeNames.DontGenerate) == null;
            data.ShouldGenerateMethods(generate);
        }
    }

    public sealed class ShouldGenerateComponentIndexComponentDataProvider : IComponentDataProvider
    {
        public void Provide(INamedTypeSymbol type, ComponentData data)
        {
            data.ShouldGenerateIndex(type.GetAttribute(AttributeNames.DontGenerate) == null);
        }
    }

    public sealed class EventComponentDataProvider : IComponentDataProvider
    {
        public void Provide(INamedTypeSymbol type, ComponentData data)
        {
            var attrs = type.GetAttributes(AttributeNames.Event);
            if (attrs.Length > 0)
            {
                data.IsEvent(true);
                var eventData = attrs
                    .Select(attr =>
                    {
                        var args = attr.ConstructorArguments;
                        var eventTarget = (EventTarget)(int)args[0].Value!;
                        var eventType = (EventType)(int)args[1].Value!;
                        var priority = (int)args[2].Value!;
                        return new EventData(eventTarget, eventType, priority);
                    }).ToArray();

                data.SetEventData(eventData);
            }
            else
            {
                data.IsEvent(false);
            }
        }
    }
}
