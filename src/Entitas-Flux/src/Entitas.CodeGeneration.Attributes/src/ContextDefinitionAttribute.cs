using System;

namespace Entitas.CodeGeneration.Attributes
{
    [AttributeUsage(AttributeTargets.Assembly, AllowMultiple = true)]
    public sealed class ContextDefinitionAttribute : Attribute
    {
        public readonly string contextName;

        public ContextDefinitionAttribute(string contextName)
        {
            this.contextName = contextName;
        }
    }
}
