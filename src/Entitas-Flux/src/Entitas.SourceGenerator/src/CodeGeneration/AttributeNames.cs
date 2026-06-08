namespace Entitas.SourceGenerator.CodeGeneration
{
    /// <summary>
    /// Fully-qualified attribute names used for string-based attribute lookups.
    /// We deliberately do NOT reference the attribute types so the generator
    /// project stays dependency-free.
    /// </summary>
    public static class AttributeNames
    {
        const string Ns = "Entitas.CodeGeneration.Attributes.";

        public const string Unique = Ns + "UniqueAttribute";
        public const string FlagPrefix = Ns + "FlagPrefixAttribute";
        public const string Watched = Ns + "WatchedAttribute";
        public const string DontGenerate = Ns + "DontGenerateAttribute";
        public const string Event = Ns + "EventAttribute";
        public const string ComponentName = Ns + "ComponentNameAttribute";
        public const string Cleanup = Ns + "CleanupAttribute";
        public const string CustomEntityIndex = Ns + "CustomEntityIndexAttribute";
        public const string EntityIndexGetMethod = Ns + "EntityIndexGetMethodAttribute";
        public const string EntityIndex = Ns + "EntityIndexAttribute";
        public const string PrimaryEntityIndex = Ns + "PrimaryEntityIndexAttribute";
        public const string AbstractEntityIndex = Ns + "AbstractEntityIndexAttribute";
        public const string Context = Ns + "ContextAttribute";
        public const string ContextDefinition = Ns + "ContextDefinitionAttribute";
        public const string EntitasGeneration = Ns + "EntitasGenerationAttribute";
        public const string DisableEntitasGenerator = Ns + "DisableEntitasGeneratorAttribute";
    }

    public static class WellKnownTypes
    {
        public const string ComponentInterface = "Entitas.IComponent";
    }
}
