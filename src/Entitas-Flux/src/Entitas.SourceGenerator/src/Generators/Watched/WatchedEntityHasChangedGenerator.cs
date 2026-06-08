using System.IO;
using System.Linq;

namespace Entitas.SourceGenerator.CodeGeneration
{
    public sealed class WatchedEntityHasChangedGenerator : AbstractGenerator
    {
        public override string Name => "Entity (HasChanged for Watched Components)";

        const string Template =
@"public sealed partial class ${EntityType} {

    /// <summary>
    /// Indicates whether the component with the given ID has changed since the last network tick.
    /// </summary>
    public bool HasChanged(int lookupComponentId) => lookupComponentId switch {
${hasChangedCases}
        _ => false
    };

    /// <summary>
    /// Indicates whether the given component ID corresponds to a ""changed"" flag component, like `CurrentHealthChanged`.
    /// </summary>
    public bool ComponentIsChangedFlag(int lookupComponentId) => lookupComponentId switch {
${isChangedFlagCases}
        _ => false
    };

    /// <summary>
    /// Given a ""changed"" flag component ID, returns the original component ID it corresponds to.
    /// </summary>
    public int GetOriginalComponentIdFromChangeFlag(int lookupComponentId) => lookupComponentId switch {
${originalIdFromFlagCases}
        _ => throw new System.ArgumentException($""Component ID {lookupComponentId} is not a recognized 'changed' flag component."")
    };

    /// <summary>
    /// Indicates whether the component with the given ID is a watched component.
    /// </summary>
    public bool IsWatchedComponent(int lookupComponentId) => lookupComponentId switch {
${isWatchedComponentCases}
        _ => false
    };

    /// <summary>
    /// Given a component ID, returns the corresponding ""changed"" flag component ID.
    /// </summary>
    public int GetChangedFlagIdOf(int lookupComponentId) => lookupComponentId switch {
${changedFlagIdOfCases}
        _ => throw new System.ArgumentException($""Component ID {lookupComponentId} does not have a corresponding 'changed' flag component."")
    };

    /// <summary>
    /// Given a component ID, returns the corresponding ""changed"" flag component Type.
    /// </summary>
    public System.Type GetChangeFlagTypeOf(int changeComponentTypeId) => changeComponentTypeId switch {
${changeFlagTypeOfCases}
        _ => throw new System.ArgumentException($""Component ID {changeComponentTypeId} does not have a corresponding 'changed' flag component type."")
    };
}
";

        public override CodeGenFile[] Generate(CodeGeneratorData[] data)
        {
            var watched = data
                .OfType<ComponentData>()
                .Where(d => d.ShouldWatchChanges())
                .ToArray();

            if (watched.Length == 0)
                return new CodeGenFile[0];

            var byContext = watched
                .SelectMany(d => d.GetContextNames().Select(ctx => new { ctx, d }))
                .GroupBy(x => x.ctx, x => x.d);

            return byContext
                .Select(g => Generate(g.Key, g.ToArray()))
                .ToArray();
        }

        CodeGenFile Generate(string contextName, ComponentData[] watched)
        {
            var lookupType = contextName + CodeGeneratorExtensions.LOOKUP;

            watched = watched.OrderBy(d => d.ComponentName()).ToArray();

            var hasChangedCases = string.Join("\n", watched
                .Select(d =>
                    $"        {lookupType}.{d.ComponentName()} => is{d.ComponentName()}Changed,"));

            var isChangedFlagCases = string.Join("\n", watched
                .Select(d =>
                    $"        {lookupType}.{d.ComponentName()}Changed => true,"));

            var originalIdFromFlagCases = string.Join("\n", watched
                .Select(d =>
                    $"        {lookupType}.{d.ComponentName()}Changed => {lookupType}.{d.ComponentName()},"));

            var isWatchedComponentCases = string.Join("\n", watched
                .Select(d =>
                    $"        {lookupType}.{d.ComponentName()} => true,"));

            var changedFlagIdOfCases = string.Join("\n", watched
                .Select(d =>
                    $"        {lookupType}.{d.ComponentName()} => {lookupType}.{d.ComponentName()}Changed,"));

            var changeFlagTypeOfCases = string.Join("\n", watched
                .Select(d =>
                    $"        {lookupType}.{d.ComponentName()} => typeof(global::{d.ComponentName()}Changed),"));

            var fileContent = Template
                .Replace("${EntityType}", contextName.AddEntitySuffix())
                .Replace("${hasChangedCases}", hasChangedCases)
                .Replace("${isChangedFlagCases}", isChangedFlagCases)
                .Replace("${originalIdFromFlagCases}", originalIdFromFlagCases)
                .Replace("${isWatchedComponentCases}", isWatchedComponentCases)
                .Replace("${changedFlagIdOfCases}", changedFlagIdOfCases)
                .Replace("${changeFlagTypeOfCases}", changeFlagTypeOfCases);

            var fileName = $"{contextName.AddEntitySuffix()}.HasChanged.cs";

            return new CodeGenFile(
                contextName + Path.DirectorySeparatorChar +
                "Entities" + Path.DirectorySeparatorChar +
                fileName,
                fileContent,
                GetType().FullName
            );
        }
    }
}
