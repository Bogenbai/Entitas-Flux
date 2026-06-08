using System.Collections.Generic;
using System.Linq;
using Entitas.SourceGenerator.CodeGeneration;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

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

        public string[] GetContextNames(INamedTypeSymbol type)
        {
            var contextNames = new List<string>();
            foreach (var attribute in type.GetAttributes())
            {
                var attributeClass = attribute.AttributeClass;
                if (attributeClass == null)
                    continue;

                // Use the SIMPLE name, not ToString(): during generation the context
                // attribute ([Game], [Inventory], …) isn't generated yet, so it's an
                // unresolved symbol. If a namespace in scope shares the context's name
                // (e.g. a `Code.Gameplay.Inventory` namespace), the unresolved [Inventory]
                // binds to that namespace and ToString() yields "Code.Gameplay.Inventory",
                // which would miss the ContextNames match and silently fall back to the
                // default context. attributeClass.Name is just "Inventory".
                var contextNameCandidate = attributeClass.Name.Replace("Attribute", string.Empty);
                if (attributeClass.BaseType == null && ContextNames.Contains(contextNameCandidate))
                {
                    // Possible compiler error. Just take the attribute name.
                    contextNames.Add(contextNameCandidate);
                }
                else if (attributeClass.BaseType != null && attributeClass.BaseType.ToCompilableString() == AttributeNames.Context)
                {
                    // Generated context attribute (derives from ContextAttribute):
                    // read the literal passed to its base constructor.
                    var name = TryGetGeneratedContextName(attribute);
                    if (name != null)
                        contextNames.Add(name);
                }
                else if (attributeClass.ToCompilableString().Contains(AttributeNames.Context))
                {
                    // Entitas.CodeGeneration.Attributes.ContextAttribute used directly.
                    var name = (string)attribute.ConstructorArguments.First().Value!;
                    contextNames.Add(name);
                }
            }

            return contextNames.ToArray();
        }

        public string[] GetContextNamesOrDefault(INamedTypeSymbol type)
        {
            var contextNames = GetContextNames(type);
            if (contextNames.Length == 0)
                contextNames = new[] { ContextNames[0] };

            return contextNames;
        }

        static string? TryGetGeneratedContextName(AttributeData attribute)
        {
            var ctor = attribute.AttributeConstructor;
            var syntaxRef = ctor?.DeclaringSyntaxReferences.FirstOrDefault();
            if (syntaxRef == null)
                return null;

            var declaration = syntaxRef.GetSyntax();
            var baseInit = declaration.DescendantNodes()
                .FirstOrDefault(node => node.IsKind(SyntaxKind.BaseConstructorInitializer))
                as ConstructorInitializerSyntax;
            if (baseInit == null)
                return null;

            var argument = baseInit.ArgumentList.Arguments.FirstOrDefault()?.Expression as LiteralExpressionSyntax;
            if (argument == null)
                return null;

            return argument.ToString().Replace("\"", string.Empty);
        }
    }
}
