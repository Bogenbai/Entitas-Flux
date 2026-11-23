using Entitas.CodeGeneration.Attributes;

// A space port with its own naming: no *Request or *Event anywhere, no Game context, the plain
// (not atomic) entity API. Everything the tests expect of it comes from structure.
[assembly: ContextDefinition("Port")]
[assembly: ContextDefinition("Signal")]
[assembly: EntitasGeneration(IgnoreNamespaces = true)]
