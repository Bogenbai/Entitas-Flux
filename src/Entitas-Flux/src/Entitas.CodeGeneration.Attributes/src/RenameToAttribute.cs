using System;

namespace Entitas.CodeGeneration.Attributes
{
    /// <summary>
    /// Marks a component as "to be renamed to <see cref="newName"/>".
    ///
    /// The attribute is deliberately INERT: code generation ignores it completely, so
    /// the project keeps compiling under the old name until the rename is actually
    /// carried out. It only carries the old -> new pair, in code, for the tooling that
    /// performs the rename (the IDE quick fix, or entitas-rename) — which also removes
    /// the attribute as part of the rename.
    /// </summary>
    [AttributeUsage(AttributeTargets.Class | AttributeTargets.Struct)]
    public class RenameToAttribute : Attribute
    {
        public readonly string newName;

        public RenameToAttribute(string newName)
        {
            this.newName = newName;
        }
    }
}
