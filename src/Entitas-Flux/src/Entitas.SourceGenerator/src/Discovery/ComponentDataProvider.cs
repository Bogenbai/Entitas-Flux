using System.Linq;
using Entitas.SourceGenerator.CodeGeneration;
using Microsoft.CodeAnalysis;

namespace Entitas.SourceGenerator.Discovery
{
    /// <summary>
    /// Orchestrator ported from the Roslyn ComponentDataProvider. Takes the
    /// discovered types and the context resolver and returns the merged
    /// ComponentData[] (components + [ComponentName] non-components + events +
    /// tracking-changes), preserving the legacy merge precedence.
    /// </summary>
    public sealed class ComponentDataProvider
    {
        readonly INamedTypeSymbol[] _types;
        readonly ContextResolver _contextResolver;
        readonly IComponentDataProvider[] _dataProviders;
        readonly bool _ignoreNamespaces;

        public ComponentDataProvider(INamedTypeSymbol[] types, ContextResolver contextResolver, bool ignoreNamespaces = false)
        {
            _types = types;
            _contextResolver = contextResolver;
            _ignoreNamespaces = ignoreNamespaces;
            _dataProviders = GetComponentDataProviders(contextResolver);
        }

        static IComponentDataProvider[] GetComponentDataProviders(ContextResolver contextResolver) => new IComponentDataProvider[]
        {
            new ComponentTypeComponentDataProvider(),
            new MemberDataComponentDataProvider(),
            new ContextsComponentDataProvider(contextResolver),
            new IsUniqueComponentDataProvider(),
            new FlagPrefixComponentDataProvider(),
            new ShouldWatchChangesComponentDataProvider(),
            new ShouldGenerateComponentComponentDataProvider(),
            new ShouldGenerateMethodsComponentDataProvider(),
            new ShouldGenerateComponentIndexComponentDataProvider(),
            new EventComponentDataProvider()
        };

        public ComponentData[] GetData()
        {
            // CodeGeneratorExtensions reads ignoreNamespaces statically (faithful to
            // the legacy plugin), so set it before building any data.
            CodeGeneratorExtensions.ignoreNamespaces = _ignoreNamespaces;

            var componentInterface = WellKnownTypes.ComponentInterface;

            var dataFromComponents = _types
                .Where(type => type.AllInterfaces.Any(i => i.ToCompilableString() == componentInterface))
                .Where(type => !type.IsAbstract)
                .Select(CreateDataForComponent)
                .ToArray();

            var dataFromNonComponents = _types
                .Where(type => !type.AllInterfaces.Any(i => i.ToCompilableString() == componentInterface))
                .Where(type => !type.IsGenericType)
                .Where(HasContexts)
                .SelectMany(CreateDataForNonComponent)
                .ToArray();

            var mergedData = Merge(dataFromNonComponents, dataFromComponents);

            var dataFromEvents = mergedData
                .Where(data => data.IsEvent())
                .SelectMany(CreateDataForEvents)
                .ToArray();

            mergedData = Merge(dataFromEvents, mergedData);

            var dataFromTrackingChanges = mergedData
                .Where(data => data.ShouldWatchChanges())
                .SelectMany(CreateDataForWatched)
                .GroupBy(d => d.GetTypeName())
                .Select(g =>
                {
                    var first = g.First();
                    first.SetContextNames(g.SelectMany(d => d.GetContextNames()).Distinct().OrderBy(ctx => ctx).ToArray());
                    return first;
                })
                .ToArray();

            return Merge(dataFromTrackingChanges, mergedData);
        }

        static ComponentData[] Merge(ComponentData[] prioData, ComponentData[] redundantData)
        {
            var lookup = prioData.ToLookup(data => data.GetTypeName());
            return redundantData
                .Where(data => !lookup.Contains(data.GetTypeName()))
                .Concat(prioData)
                .ToArray();
        }

        ComponentData CreateDataForComponent(INamedTypeSymbol type)
        {
            var data = new ComponentData();
            foreach (var provider in _dataProviders)
                provider.Provide(type, data);

            return data;
        }

        ComponentData[] CreateDataForNonComponent(INamedTypeSymbol type) => GetComponentNames(type)
            .Select(componentName =>
            {
                var data = CreateDataForComponent(type);
                data.SetTypeName(componentName.AddComponentSuffix());
                data.SetMemberData(new[]
                {
                    new MemberData(type.ToCompilableString(), "value")
                });

                return data;
            }).ToArray();

        ComponentData[] CreateDataForEvents(ComponentData data) => data.GetContextNames()
            .SelectMany(contextName =>
                data.GetEventData().Select(eventData =>
                {
                    var dataForEvent = new ComponentData(data);
                    dataForEvent.IsEvent(false);
                    dataForEvent.IsUnique(false);
                    dataForEvent.ShouldGenerateComponent(false);
                    dataForEvent.ShouldWatchChanges(false);
                    var eventComponentName = data.EventComponentName(eventData);
                    var eventTypeSuffix = eventData.GetEventTypeSuffix();
                    var optionalContextName = dataForEvent.GetContextNames().Length > 1 ? contextName : string.Empty;
                    var listenerComponentName = optionalContextName + eventComponentName + eventTypeSuffix.AddListenerSuffix();
                    dataForEvent.SetTypeName(listenerComponentName.AddComponentSuffix());
                    dataForEvent.SetMemberData(new[]
                    {
                        new MemberData($"System.Collections.Generic.List<I{listenerComponentName}>", "value")
                    });
                    dataForEvent.SetContextNames(new[] { contextName });
                    return dataForEvent;
                }).ToArray()
            ).ToArray();

        ComponentData[] CreateDataForWatched(ComponentData data) => data.GetContextNames()
            .Select(contextName =>
            {
                var dataForTrackingChanges = new ComponentData(data);
                dataForTrackingChanges.IsEvent(false);
                dataForTrackingChanges.IsUnique(false);
                dataForTrackingChanges.ShouldGenerateComponent(false);
                dataForTrackingChanges.ShouldWatchChanges(false);
                var trackingChangesComponentName = data.TrackingChangesComponentName();
                dataForTrackingChanges.SetTypeName(trackingChangesComponentName);
                dataForTrackingChanges.SetMemberData(new MemberData[0]);
                dataForTrackingChanges.SetContextNames(new[] { contextName });
                return dataForTrackingChanges;
            }).ToArray();

        bool HasContexts(INamedTypeSymbol type) => _contextResolver.GetContextNames(type).Length != 0;

        string[] GetComponentNames(INamedTypeSymbol type)
        {
            var attr = type.GetAttribute(AttributeNames.ComponentName);
            if (attr == null)
                return new[] { type.ToCompilableString().TypeName().AddComponentSuffix() };

            return attr.ConstructorArguments.First().Values.Select(arg => (string)arg.Value!).ToArray();
        }
    }
}
