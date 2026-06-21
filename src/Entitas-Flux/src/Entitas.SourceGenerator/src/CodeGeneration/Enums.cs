namespace Entitas.SourceGenerator.CodeGeneration
{
    // These mirror the originals in Entitas.CodeGeneration.Attributes. When we read
    // AttributeData.ConstructorArguments[i].Value the boxed value is the underlying
    // int, so callers cast through int to these local enums.

    public enum EventTarget
    {
        Any = 0,
        Self = 1
    }

    public enum EventType
    {
        Added = 0,
        Removed = 1
    }

    public enum CleanupMode
    {
        RemoveComponent = 0,
        DestroyEntity = 1
    }
}
