using System.Linq;
using Entitas.SourceGenerator.CodeGeneration;

namespace Entitas.SourceGenerator.Discovery
{
    public interface IComponentDataProvider
    {
        void Provide(TypeSnapshot type, ComponentData data);
    }

    public sealed class ComponentTypeComponentDataProvider : IComponentDataProvider
    {
        public void Provide(TypeSnapshot type, ComponentData data)
        {
            data.SetTypeName(type.FullName);
        }
    }

    public sealed class MemberDataComponentDataProvider : IComponentDataProvider
    {
        public void Provide(TypeSnapshot type, ComponentData data)
        {
            data.SetMemberData(type.Members
                .Select(member => new MemberData(member.TypeName, member.Name))
                .ToArray());
        }
    }

    public sealed class ContextsComponentDataProvider : IComponentDataProvider
    {
        readonly ContextResolver _resolver;

        public ContextsComponentDataProvider(ContextResolver resolver) => _resolver = resolver;

        public void Provide(TypeSnapshot type, ComponentData data)
        {
            data.SetContextNames(_resolver.GetContextNamesOrDefault(type));
        }
    }

    public sealed class IsUniqueComponentDataProvider : IComponentDataProvider
    {
        public void Provide(TypeSnapshot type, ComponentData data)
        {
            var isUnique = type.GetAttribute(AttributeNames.Unique) != null;
            data.IsUnique(isUnique);
        }
    }

    public sealed class FlagPrefixComponentDataProvider : IComponentDataProvider
    {
        public void Provide(TypeSnapshot type, ComponentData data)
        {
            data.SetFlagPrefix(GetCustomComponentPrefix(type));
        }

        static string GetCustomComponentPrefix(TypeSnapshot type)
        {
            var attr = type.GetAttribute(AttributeNames.FlagPrefix);
            return attr == null ? "is" : attr.Arguments.FirstOrDefault() ?? "is";
        }
    }

    public sealed class ShouldWatchChangesComponentDataProvider : IComponentDataProvider
    {
        public void Provide(TypeSnapshot type, ComponentData data)
        {
            var shouldTrackChanges = type.GetAttribute(AttributeNames.Watched) != null;
            data.ShouldWatchChanges(shouldTrackChanges);
        }
    }

    public sealed class ShouldGenerateComponentComponentDataProvider : IComponentDataProvider
    {
        public void Provide(TypeSnapshot type, ComponentData data)
        {
            var shouldGenerateComponent = !type.IsComponent;
            data.ShouldGenerateComponent(shouldGenerateComponent);
            if (shouldGenerateComponent)
                data.SetObjectTypeName(type.FullName);
        }
    }

    public sealed class ShouldGenerateMethodsComponentDataProvider : IComponentDataProvider
    {
        public void Provide(TypeSnapshot type, ComponentData data)
        {
            var generate = type.GetAttribute(AttributeNames.DontGenerate) == null;
            data.ShouldGenerateMethods(generate);
        }
    }

    public sealed class ShouldGenerateComponentIndexComponentDataProvider : IComponentDataProvider
    {
        public void Provide(TypeSnapshot type, ComponentData data)
        {
            data.ShouldGenerateIndex(type.GetAttribute(AttributeNames.DontGenerate) == null);
        }
    }

    public sealed class EventComponentDataProvider : IComponentDataProvider
    {
        public void Provide(TypeSnapshot type, ComponentData data)
        {
            var attrs = type.GetAttributes(AttributeNames.Event);
            if (attrs.Length > 0)
            {
                data.IsEvent(true);
                var eventData = attrs
                    .Select(attr =>
                    {
                        var args = attr.Arguments;
                        var eventTarget = (EventTarget)int.Parse(args[0]!);
                        var eventType = (EventType)int.Parse(args[1]!);
                        var priority = int.Parse(args[2]!);
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
