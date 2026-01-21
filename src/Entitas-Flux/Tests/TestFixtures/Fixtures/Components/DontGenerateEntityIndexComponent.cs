using Entitas;
using Entitas.CodeGeneration.Attributes;

[Context("Test"), DontGenerate]
public sealed class DontGenerateEntityIndexComponent : IComponent
{
	[EntityIndex]
	public string value;
}
