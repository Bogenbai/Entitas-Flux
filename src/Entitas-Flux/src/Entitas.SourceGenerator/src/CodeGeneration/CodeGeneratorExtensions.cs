using System;
using System.Linq;
using Microsoft.CodeAnalysis.CSharp;

namespace Entitas.SourceGenerator.CodeGeneration
{
    /// <summary>
    /// Ported verbatim (in logic) from the legacy
    /// Entitas.CodeGeneration.Plugins.CodeGeneratorExtensions. Differences:
    ///  - DesperateDevs.Extensions string helpers are replaced by StringExtensions.
    ///  - Entitas.CodeGeneration.Attributes enums are replaced by local enums.
    ///  - CodeDomProvider.IsValidIdentifier is replaced by SyntaxFacts.IsValidIdentifier.
    /// NOTE: ignoreNamespaces mirrors the original's mutable-static design, but is
    /// [ThreadStatic] so concurrent generator runs (Roslyn may invoke source-output
    /// callbacks for different compilations on different threads in one process)
    /// don't clobber each other. Each run sets it on its own thread before any read,
    /// and all generators for that run execute synchronously on the same thread.
    /// </summary>
    public static class CodeGeneratorExtensions
    {
        public const string LOOKUP = "ComponentsLookup";

        const string KEYWORD_PREFIX = "@";

        [System.ThreadStatic] public static bool ignoreNamespaces;

        // [ThreadStatic] like ignoreNamespaces; set per run before generators emit. When true,
        // the entity-API generators inject partial OnAdd{X}/OnReplace{X} debug hooks.
        [System.ThreadStatic] public static bool debugHooks;

        public static string ComponentName(this ComponentData data) =>
            data.GetTypeName().ToComponentName(ignoreNamespaces);

        public static string ComponentNameValidLowerFirst(this ComponentData data) =>
            ComponentName(data).ToLowerFirst().AddPrefixIfIsKeyword();

        public static string ComponentNameWithContext(this ComponentData data, string contextName) =>
            contextName + data.ComponentName();

        public static string Replace(this string template, string contextName) => template
            .Replace("${ContextName}", contextName)
            .Replace("${contextName}", contextName.ToLowerFirst())
            .Replace("${ContextType}", contextName.AddContextSuffix())
            .Replace("${EntityType}", contextName.AddEntitySuffix())
            .Replace("${MatcherType}", contextName.AddMatcherSuffix())
            .Replace("${Lookup}", contextName + LOOKUP);

        public static string Replace(this string template, ComponentData data, string contextName) => template
            .Replace(contextName)
            .Replace("${ComponentType}", data.GetTypeName())
            .Replace("${ComponentName}", data.ComponentName())
            .Replace("${componentName}", data.ComponentName().ToLowerFirst())
            .Replace("${validComponentName}", data.ComponentNameValidLowerFirst())
            .Replace("${prefixedComponentName}", data.PrefixedComponentName())
            .Replace("${newMethodParameters}", GetMethodParameters(data.GetMemberData(), true))
            .Replace("${methodParameters}", GetMethodParameters(data.GetMemberData(), false))
            .Replace("${newMethodArgs}", GetMethodArgs(data.GetMemberData(), true))
            .Replace("${methodArgs}", GetMethodArgs(data.GetMemberData(), false))
            .Replace("${Index}", $"{contextName}{LOOKUP}.{data.ComponentName()}");

        public static string Replace(this string template, ComponentData data, string contextName, EventData eventData)
        {
            var eventListener = data.EventListener(contextName, eventData);
            return template
                .Replace(data, contextName)
                .Replace("${EventComponentName}", data.EventComponentName(eventData))
                .Replace("${EventListenerComponent}", eventListener.AddComponentSuffix())
                .Replace("${Event}", data.Event(contextName, eventData))
                .Replace("${EventListener}", eventListener)
                .Replace("${eventListener}", eventListener.ToLowerFirst())
                .Replace("${EventType}", GetEventTypeSuffix(eventData));
        }

        public static string PrefixedComponentName(this ComponentData data) =>
            data.GetFlagPrefix().ToLowerFirst() + data.ComponentName();

        public static string Event(this ComponentData data, string contextName, EventData eventData)
        {
            var optionalContextName = data.GetContextNames().Length > 1 ? contextName : string.Empty;
            return optionalContextName + EventComponentName(data, eventData) + GetEventTypeSuffix(eventData);
        }

        public static string EventListener(this ComponentData data, string contextName, EventData eventData) =>
            data.Event(contextName, eventData).AddListenerSuffix();

        public static string EventComponentName(this ComponentData data, EventData eventData)
        {
            var componentName = data.GetTypeName().ToComponentName(ignoreNamespaces);
            var shortComponentName = data.GetTypeName().ToComponentName(true);
            var eventComponentName = componentName.Replace(
                shortComponentName,
                eventData.GetEventPrefix() + shortComponentName
            );
            return eventComponentName;
        }

        /// <summary>
        /// Name of the generated {X}Changed marker component. It must match the class
        /// WatchedComponentGenerator emits, which is built from the FLATTENED component
        /// name — using the short name here made every generated reference to the marker
        /// (lookup entry, matcher, cleanup system, the `is…Changed` assignment) point at
        /// a type that does not exist, so [Watched] simply did not compile for a
        /// component declared inside a namespace unless IgnoreNamespaces was on.
        /// </summary>
        public static string TrackingChangesComponentName(this ComponentData data) =>
            data.ComponentName() + "Changed";

        public static string GetEventMethodArgs(this ComponentData data, EventData eventData, string args)
        {
            if (data.GetMemberData().Length == 0)
                return string.Empty;

            return eventData.eventType == EventType.Removed
                ? string.Empty
                : args;
        }

        public static string GetEventTypeSuffix(this EventData eventData) =>
            eventData.eventType == EventType.Removed ? "Removed" : string.Empty;

        public static string GetEventPrefix(this EventData eventData) =>
            eventData.eventTarget == EventTarget.Any ? "Any" : string.Empty;

        public static string GetMethodParameters(this MemberData[] memberData, bool newPrefix) => string.Join(", ", memberData
            .Select(info => info.type + (newPrefix ? $" new{info.name.ToUpperFirst()}" : $" {info.name.ToLowerFirst()}")));

        public static string GetMethodArgs(MemberData[] memberData, bool newPrefix) => string.Join(", ", memberData
            .Select(info => newPrefix ? $"new{info.name.ToUpperFirst()}" : info.name));

        public static string AddPrefixIfIsKeyword(this string name)
        {
            if (!SyntaxFacts.IsValidIdentifier(name))
                name = KEYWORD_PREFIX + name;

            return name;
        }

        public static bool IsAtomicComponent(this MemberData[] members) =>
            members.Length == 1 &&
            string.Compare(members[0].name, "Value", StringComparison.InvariantCulture) == 0;
    }
}
