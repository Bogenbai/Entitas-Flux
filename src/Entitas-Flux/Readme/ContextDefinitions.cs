using Entitas.CodeGeneration.Attributes;

// Declares the contexts for the Entitas source generator. This replaces the
// legacy Jenny config line `Entitas.CodeGeneration.Plugins.Contexts = Game, Input, GameState`.
// Order matters: the first context is the default for components without an
// explicit context attribute.
[assembly: ContextDefinition("Game")]
[assembly: ContextDefinition("Input")]
[assembly: ContextDefinition("GameState")]
