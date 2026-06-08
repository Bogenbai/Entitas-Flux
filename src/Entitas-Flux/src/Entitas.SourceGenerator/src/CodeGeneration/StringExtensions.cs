using System.Text.RegularExpressions;

namespace Entitas.SourceGenerator.CodeGeneration
{
    /// <summary>
    /// Local reimplementation of the DesperateDevs.Extensions string helpers the
    /// legacy plugins relied on. Kept dependency-free; behavior mirrors the
    /// originals (regex anchors, suffix add/remove, first-char casing).
    /// </summary>
    public static class StringExtensions
    {
        public static string TypeName(this string fullTypeName)
        {
            var index = fullTypeName.LastIndexOf('.');
            return index < 0 ? fullTypeName : fullTypeName.Substring(index + 1);
        }

        public static string RemoveDots(this string fullTypeName) => fullTypeName.Replace(".", string.Empty);

        public static string RemoveComponentSuffix(this string componentName) =>
            Regex.Replace(componentName, "Component$", string.Empty);

        public static string AddComponentSuffix(this string componentName) =>
            componentName.RemoveComponentSuffix() + "Component";

        public static string RemoveContextSuffix(this string contextName) =>
            Regex.Replace(contextName, "Context$", string.Empty);

        public static string AddContextSuffix(this string contextName) =>
            contextName.RemoveContextSuffix() + "Context";

        public static string AddEntitySuffix(this string contextName) => contextName + "Entity";

        public static string AddMatcherSuffix(this string contextName) => contextName + "Matcher";

        public static string AddSystemSuffix(this string str) =>
            str.EndsWith("System") ? str : str + "System";

        public static string AddListenerSuffix(this string listenerName) => listenerName + "Listener";

        public static bool HasListenerSuffix(this string listenerName) => listenerName.EndsWith("Listener");

        public static string ToLowerFirst(this string str)
        {
            if (string.IsNullOrEmpty(str))
                return str;

            return char.ToLower(str[0]) + str.Substring(1);
        }

        public static string ToUpperFirst(this string str)
        {
            if (string.IsNullOrEmpty(str))
                return str;

            return char.ToUpper(str[0]) + str.Substring(1);
        }

        public static string ToComponentName(this string fullTypeName, bool ignoreNamespaces) => ignoreNamespaces
            ? fullTypeName.TypeName().RemoveComponentSuffix()
            : fullTypeName.RemoveDots().RemoveComponentSuffix();
    }
}
