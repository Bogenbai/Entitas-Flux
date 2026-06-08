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

    public class MemberData
    {
        public readonly string type;
        public readonly string name;

        public MemberData(string type, string name)
        {
            this.type = type;
            this.name = name;
        }
    }

    public class MethodData
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
    }

    public class EventData
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
    }
}
