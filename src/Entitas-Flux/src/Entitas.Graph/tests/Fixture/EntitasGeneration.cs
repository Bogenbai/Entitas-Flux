using Entitas.CodeGeneration.Attributes;

[assembly: ContextDefinition("Game")]
[assembly: ContextDefinition("Input")]
[assembly: EntitasGeneration(EntityApi = EntityApiStyle.Atomic, IgnoreNamespaces = true)]
