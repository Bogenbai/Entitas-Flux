using System;

namespace Entitas.CodeGeneration.Attributes
{
	/// <summary>
	/// Use to automatically mark entity with "${ComponentName}Changed" component on this component change.
	/// </summary>
	[AttributeUsage(AttributeTargets.Interface | AttributeTargets.Class | AttributeTargets.Struct | AttributeTargets.Enum)]
	public class WatchedAttribute : Attribute
	{
		
	}
}
