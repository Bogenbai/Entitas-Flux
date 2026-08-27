using System.Collections.Generic;
using System.Linq;
using Entitas.SourceGenerator.CodeGeneration;
using Microsoft.CodeAnalysis;

namespace Entitas.SourceGenerator.Discovery
{
    /// <summary>
    /// Ports the legacy ContextsComponentDataProvider context-resolution logic.
    /// The ordered context-name list (which legacy read from Jenny config
    /// "Contexts = Game,Input,...") is now supplied from assembly-level
    /// [ContextDefinition("X")] attributes; first entry is the default context.
    /// </summary>
    public sealed class ContextResolver
    {
        public string[] ContextNames { get; }

        public ContextResolver(string[] contextNames) => ContextNames = contextNames;

        public static ContextResolver FromCompilation(Compilation compilation)
        {
            var contextNames = compilation.Assembly.GetAttributes()
                .Where(attr => attr.AttributeClass?.ToCompilableString() == AttributeNames.ContextDefinition)
                .Select(attr => attr.ConstructorArguments.FirstOrDefault().Value as string)
                .Where(name => !string.IsNullOrEmpty(name))
                .Select(name => name!)
                .ToArray();

            return new ContextResolver(contextNames);
        }

        public string[] GetContextNames(TypeSnapshot type)
        {
            var contextNames = new List<string>();
            foreach (var attribute in type.Attributes)
            {
                if (attribute.TypeNames.Length == 0)
                    continue;

                // Use the SIMPLE name, not the full one: during generation the context
                // attribute ([Game], [Inventory], …) isn't generated yet, so it's an
                // unresolved symbol. If a namespace in scope shares the context's name
                // (e.g. a `Code.Gameplay.Inventory` namespace), the unresolved [Inventory]
                // binds to that namespace and the full name yields
                // "Code.Gameplay.Inventory", which would miss the ContextNames match and
                // silently fall back to the default context.
                var contextNameCandidate = attribute.SimpleName.Replace("Attribute", string.Empty);
                if (!attribute.HasBaseType && ContextNames.Contains(contextNameCandidate))
                {
                    // Possible compiler error. Just take the attribute name.
                    contextNames.Add(contextNameCandidate);
                }
                else if (attribute.HasBaseType && attribute.BaseTypeName == AttributeNames.Context)
                {
                    // Generated context attribute (derives from ContextAttribute):
                    // the literal passed to its base constructor.
                    if (attribute.ContextLiteral != null)
                        contextNames.Add(attribute.ContextLiteral);
                }
                else if (attribute.FullName.Contains(AttributeNames.Context))
                {
                    // Entitas.CodeGeneration.Attributes.ContextAttribute used directly.
                    var name = attribute.Arguments.FirstOrDefault();
                    if (name != null)
                        contextNames.Add(name);
                }
            }

            return contextNames.ToArray();
        }

        public string[] GetContextNamesOrDefault(TypeSnapshot type)
        {
            var contextNames = GetContextNames(type);
            if (contextNames.Length == 0)
                contextNames = new[] { ContextNames[0] };

            return contextNames;
        }

    }
}
