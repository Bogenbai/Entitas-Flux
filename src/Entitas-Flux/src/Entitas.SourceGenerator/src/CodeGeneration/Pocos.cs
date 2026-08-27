using System;

namespace Entitas.SourceGenerator.CodeGeneration
{
    public class ComponentData : CodeGeneratorData
    {
        public ComponentData() { }

        public ComponentData(CodeGeneratorData data) : base(data) { }
    }

    public class ContextData : CodeGeneratorData { }

    public class EntityIndexData : CodeGeneratorData { }

    public sealed class CleanupData : CodeGeneratorData
    {
        public const string CLEANUP_MODE = "Cleanup.Mode";

        public CleanupMode cleanupMode
        {
            get => (CleanupMode)this[CLEANUP_MODE];
            set => this[CLEANUP_MODE] = value;
        }

        public ComponentData componentData => _componentData;

        readonly ComponentData _componentData;

        public CleanupData(CodeGeneratorData data) : base(data)
        {
            _componentData = (ComponentData)data;
        }
    }

    public sealed class WatchedCleanupData : CodeGeneratorData
    {
        public ComponentData componentData => _componentData;

        readonly ComponentData _componentData;

        public WatchedCleanupData(CodeGeneratorData data) : base(data)
        {
            _componentData = (ComponentData)data;
        }
    }

    public class MemberData : IEquatable<MemberData>
    {
        public readonly string type;
        public readonly string name;

        public MemberData(string type, string name)
        {
            this.type = type;
            this.name = name;
        }

        public bool Equals(MemberData? other) =>
            other != null &&
            string.Equals(type, other.type, StringComparison.Ordinal) &&
            string.Equals(name, other.name, StringComparison.Ordinal);

        public override bool Equals(object? obj) => Equals(obj as MemberData);

        public override int GetHashCode() => unchecked(
            StringComparer.Ordinal.GetHashCode(type) * 397 ^ StringComparer.Ordinal.GetHashCode(name));
    }

    public class MethodData : IEquatable<MethodData>
    {
        public readonly string returnType;
        public readonly string methodName;
        public readonly MemberData[] parameters;

        public MethodData(string returnType, string methodName, MemberData[] parameters)
        {
            this.returnType = returnType;
            this.methodName = methodName;
            this.parameters = parameters;
        }

        public bool Equals(MethodData? other)
        {
            if (other == null ||
                !string.Equals(returnType, other.returnType, StringComparison.Ordinal) ||
                !string.Equals(methodName, other.methodName, StringComparison.Ordinal) ||
                parameters.Length != other.parameters.Length)
                return false;

            for (var i = 0; i < parameters.Length; i++)
            {
                if (!parameters[i].Equals(other.parameters[i]))
                    return false;
            }

            return true;
        }

        public override bool Equals(object? obj) => Equals(obj as MethodData);

        public override int GetHashCode() => unchecked(
            StringComparer.Ordinal.GetHashCode(methodName) * 397 ^ parameters.Length);
    }

    public class EventData : IEquatable<EventData>
    {
        public readonly EventTarget eventTarget;
        public readonly EventType eventType;
        public readonly int priority;

        public EventData(EventTarget eventTarget, EventType eventType, int priority)
        {
            this.eventTarget = eventTarget;
            this.eventType = eventType;
            this.priority = priority;
        }

        public bool Equals(EventData? other) =>
            other != null &&
            eventTarget == other.eventTarget &&
            eventType == other.eventType &&
            priority == other.priority;

        public override bool Equals(object? obj) => Equals(obj as EventData);

        public override int GetHashCode() => unchecked(((int)eventTarget * 397 ^ (int)eventType) * 397 ^ priority);
    }
}
