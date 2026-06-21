namespace Entitas.SourceGenerator.CodeGeneration
{
    // The dictionary key strings below are copied verbatim from the legacy Jenny
    // reflection sub-providers so that any ported generator logic keeps working.

    public static class ComponentDataExtensions
    {
        public const string COMPONENT_TYPE = "Component.TypeName";
        public static string GetTypeName(this ComponentData data) => (string)data[COMPONENT_TYPE];
        public static void SetTypeName(this ComponentData data, string fullTypeName) => data[COMPONENT_TYPE] = fullTypeName;

        public const string COMPONENT_MEMBER_DATA = "Component.MemberData";
        public static MemberData[] GetMemberData(this ComponentData data) => (MemberData[])data[COMPONENT_MEMBER_DATA];
        public static void SetMemberData(this ComponentData data, MemberData[] memberInfos) => data[COMPONENT_MEMBER_DATA] = memberInfos;

        public const string COMPONENT_CONTEXTS = "Component.ContextNames";
        public static string[] GetContextNames(this ComponentData data) => (string[])data[COMPONENT_CONTEXTS];
        public static void SetContextNames(this ComponentData data, string[] contextNames) => data[COMPONENT_CONTEXTS] = contextNames;

        public const string COMPONENT_IS_UNIQUE = "Component.Unique";
        public static bool IsUnique(this ComponentData data) => (bool)data[COMPONENT_IS_UNIQUE];
        public static void IsUnique(this ComponentData data, bool isUnique) => data[COMPONENT_IS_UNIQUE] = isUnique;

        public const string COMPONENT_FLAG_PREFIX = "Component.FlagPrefix";
        public static string GetFlagPrefix(this ComponentData data) => (string)data[COMPONENT_FLAG_PREFIX];
        public static void SetFlagPrefix(this ComponentData data, string prefix) => data[COMPONENT_FLAG_PREFIX] = prefix;

        public const string COMPONENT_WATCHED = "Component.Watched";
        public static bool ShouldWatchChanges(this ComponentData data) => (bool)data[COMPONENT_WATCHED];
        public static void ShouldWatchChanges(this ComponentData data, bool isTrackingChanges) => data[COMPONENT_WATCHED] = isTrackingChanges;

        public const string COMPONENT_GENERATE_COMPONENT = "Component.Generate.Object";
        public static bool ShouldGenerateComponent(this ComponentData data) => (bool)data[COMPONENT_GENERATE_COMPONENT];
        public static void ShouldGenerateComponent(this ComponentData data, bool generate) => data[COMPONENT_GENERATE_COMPONENT] = generate;

        public const string COMPONENT_OBJECT_TYPE = "Component.ObjectTypeName";
        public static string GetObjectTypeName(this ComponentData data) => (string)data[COMPONENT_OBJECT_TYPE];
        public static void SetObjectTypeName(this ComponentData data, string type) => data[COMPONENT_OBJECT_TYPE] = type;

        public const string COMPONENT_GENERATE_INDEX = "Component.Generate.Index";
        public static bool ShouldGenerateIndex(this ComponentData data) => (bool)data[COMPONENT_GENERATE_INDEX];
        public static void ShouldGenerateIndex(this ComponentData data, bool generate) => data[COMPONENT_GENERATE_INDEX] = generate;

        public const string COMPONENT_GENERATE_METHODS = "Component.Generate.Methods";
        public static bool ShouldGenerateMethods(this ComponentData data) => (bool)data[COMPONENT_GENERATE_METHODS];
        public static void ShouldGenerateMethods(this ComponentData data, bool generate) => data[COMPONENT_GENERATE_METHODS] = generate;

        public const string COMPONENT_EVENT = "Component.Event";
        public static bool IsEvent(this ComponentData data) => (bool)data[COMPONENT_EVENT];
        public static void IsEvent(this ComponentData data, bool isEvent) => data[COMPONENT_EVENT] = isEvent;

        public const string COMPONENT_EVENT_DATA = "Component.Event.Data";
        public static EventData[] GetEventData(this ComponentData data) => (EventData[])data[COMPONENT_EVENT_DATA];
        public static void SetEventData(this ComponentData data, EventData[] eventData) => data[COMPONENT_EVENT_DATA] = eventData;
    }

    public static class ContextDataExtensions
    {
        public const string CONTEXT_NAME = "Context.Name";
        public static string GetContextName(this ContextData data) => (string)data[CONTEXT_NAME];
        public static void SetContextName(this ContextData data, string contextName) => data[CONTEXT_NAME] = contextName;
    }

    public static class EntityIndexDataExtensions
    {
        public const string ENTITY_INDEX_TYPE = "EntityIndex.Type";
        public static string GetEntityIndexType(this EntityIndexData data) => (string)data[ENTITY_INDEX_TYPE];
        public static void SetEntityIndexType(this EntityIndexData data, string type) => data[ENTITY_INDEX_TYPE] = type;

        public const string ENTITY_INDEX_IS_CUSTOM = "EntityIndex.Custom";
        public static bool IsCustom(this EntityIndexData data) => (bool)data[ENTITY_INDEX_IS_CUSTOM];
        public static void IsCustom(this EntityIndexData data, bool isCustom) => data[ENTITY_INDEX_IS_CUSTOM] = isCustom;

        public const string ENTITY_INDEX_CUSTOM_METHODS = "EntityIndex.CustomMethods";
        public static MethodData[] GetCustomMethods(this EntityIndexData data) => (MethodData[])data[ENTITY_INDEX_CUSTOM_METHODS];
        public static void SetCustomMethods(this EntityIndexData data, MethodData[] methods) => data[ENTITY_INDEX_CUSTOM_METHODS] = methods;

        public const string ENTITY_INDEX_NAME = "EntityIndex.Name";
        public static string GetEntityIndexName(this EntityIndexData data) => (string)data[ENTITY_INDEX_NAME];
        public static void SetEntityIndexName(this EntityIndexData data, string name) => data[ENTITY_INDEX_NAME] = name;

        public const string ENTITY_INDEX_CONTEXT_NAMES = "EntityIndex.ContextNames";
        public static string[] GetContextNames(this EntityIndexData data) => (string[])data[ENTITY_INDEX_CONTEXT_NAMES];
        public static void SetContextNames(this EntityIndexData data, string[] contextNames) => data[ENTITY_INDEX_CONTEXT_NAMES] = contextNames;

        public const string ENTITY_INDEX_KEY_TYPE = "EntityIndex.KeyType";
        public static string GetKeyType(this EntityIndexData data) => (string)data[ENTITY_INDEX_KEY_TYPE];
        public static void SetKeyType(this EntityIndexData data, string type) => data[ENTITY_INDEX_KEY_TYPE] = type;

        public const string ENTITY_INDEX_COMPONENT_TYPE = "EntityIndex.ComponentType";
        public static string GetComponentType(this EntityIndexData data) => (string)data[ENTITY_INDEX_COMPONENT_TYPE];
        public static void SetComponentType(this EntityIndexData data, string type) => data[ENTITY_INDEX_COMPONENT_TYPE] = type;

        public const string ENTITY_INDEX_MEMBER_NAME = "EntityIndex.MemberName";
        public static string GetMemberName(this EntityIndexData data) => (string)data[ENTITY_INDEX_MEMBER_NAME];
        public static void SetMemberName(this EntityIndexData data, string memberName) => data[ENTITY_INDEX_MEMBER_NAME] = memberName;

        public const string ENTITY_INDEX_HAS_MULTIPLE = "EntityIndex.HasMultiple";
        public static bool GetHasMultiple(this EntityIndexData data) => (bool)data[ENTITY_INDEX_HAS_MULTIPLE];
        public static void SetHasMultiple(this EntityIndexData data, bool hasMultiple) => data[ENTITY_INDEX_HAS_MULTIPLE] = hasMultiple;
    }
}
