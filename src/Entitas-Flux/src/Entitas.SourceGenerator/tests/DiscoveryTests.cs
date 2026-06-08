using System.Linq;
using Entitas.SourceGenerator.CodeGeneration;
using Entitas.SourceGenerator.Discovery;
using FluentAssertions;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Xunit;

namespace Entitas.SourceGenerator.Tests
{
    public class DiscoveryTests
    {
        // Minimal stand-in for the Entitas runtime + code-gen attributes. Defined
        // in source so the test compilation is self-contained and the generator
        // project stays dependency-free.
        const string Prelude = @"
namespace Entitas { public interface IComponent { } }
namespace Entitas.CodeGeneration.Attributes
{
    using System;
    [AttributeUsage(AttributeTargets.All, AllowMultiple = true)]
    public class ContextAttribute : Attribute { public ContextAttribute(string contextName) { } }
    [AttributeUsage(AttributeTargets.All)]
    public class UniqueAttribute : Attribute { }
    [AttributeUsage(AttributeTargets.All)]
    public class FlagPrefixAttribute : Attribute { public FlagPrefixAttribute(string prefix) { } }
    [AttributeUsage(AttributeTargets.All)]
    public class WatchedAttribute : Attribute { }
    [AttributeUsage(AttributeTargets.All)]
    public class DontGenerateAttribute : Attribute { public DontGenerateAttribute(bool generateIndex = true) { } }
    public enum EventTarget { Any, Self }
    public enum EventType { Added, Removed }
    [AttributeUsage(AttributeTargets.All, AllowMultiple = true)]
    public class EventAttribute : Attribute { public EventAttribute(EventTarget eventTarget, EventType eventType = EventType.Added, int priority = 0) { } }
    [AttributeUsage(AttributeTargets.All)]
    public class ComponentNameAttribute : Attribute { public ComponentNameAttribute(params string[] names) { } }
    public enum CleanupMode { RemoveComponent, DestroyEntity }
    [AttributeUsage(AttributeTargets.All)]
    public class CleanupAttribute : Attribute { public CleanupAttribute(CleanupMode cleanupMode) { } }
    [AttributeUsage(AttributeTargets.Assembly, AllowMultiple = true)]
    public sealed class ContextDefinitionAttribute : Attribute { public ContextDefinitionAttribute(string contextName) { } }

    // Generated-style context attributes (derive from ContextAttribute).
    public sealed class GameAttribute : ContextAttribute { public GameAttribute() : base(""Game"") { } }
    public sealed class InputAttribute : ContextAttribute { public InputAttribute() : base(""Input"") { } }
}
";

        static Compilation Compile(string assemblyAttributes, string source)
        {
            // Assembly-level attributes must precede all other top-level elements,
            // so they go first, then the prelude, then the test body.
            var full = assemblyAttributes + "\n" + Prelude + "\n" + source;
            var compilation = CSharpCompilation.Create(
                "TestAsm",
                new[] { CSharpSyntaxTree.ParseText(full) },
                new[] { MetadataReference.CreateFromFile(typeof(object).Assembly.Location) },
                new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));

            compilation.GetDiagnostics()
                .Where(d => d.Severity == DiagnosticSeverity.Error)
                .Should().BeEmpty("test source must compile cleanly");

            return compilation;
        }

        static DiscoveryResult Discover(string assemblyAttributes, string source, bool ignoreNamespaces = false) =>
            EntitasDiscovery.Discover(Compile(assemblyAttributes, source), ignoreNamespaces);

        static ComponentData Component(DiscoveryResult result, string typeName) =>
            result.Components.Single(c => c.GetTypeName() == typeName);

        const string Game = "[assembly: Entitas.CodeGeneration.Attributes.ContextDefinition(\"Game\")]";
        const string GameInput =
            "[assembly: Entitas.CodeGeneration.Attributes.ContextDefinition(\"Game\")]\n" +
            "[assembly: Entitas.CodeGeneration.Attributes.ContextDefinition(\"Input\")]";

        [Fact]
        public void PlainComponentHasTypeNameComponentNameAndKeywordMemberType()
        {
            const string src = @"
public class HealthComponent : Entitas.IComponent { public int value; }
";
            var result = Discover(Game, src);
            var c = Component(result, "HealthComponent");

            c.GetTypeName().Should().Be("HealthComponent");
            c.ComponentName().Should().Be("Health");
            c.GetMemberData().Should().ContainSingle();
            // Built-in types render as C# keywords, not System.Int32.
            c.GetMemberData()[0].type.Should().Be("int");
            c.GetMemberData()[0].name.Should().Be("value");
        }

        [Fact]
        public void NamespacedComponentRendersCompilableMemberType()
        {
            const string src = @"
namespace UnityEngine { public struct Vector3 { } }
public class VelocityComponent : Entitas.IComponent { public UnityEngine.Vector3 value; }
";
            var c = Component(Discover(Game, src), "VelocityComponent");
            c.GetMemberData()[0].type.Should().Be("UnityEngine.Vector3");
        }

        [Fact]
        public void UniqueAttributeMakesComponentUnique()
        {
            const string src = @"
[Entitas.CodeGeneration.Attributes.Unique]
public class GameBoardComponent : Entitas.IComponent { public int value; }
";
            Component(Discover(Game, src), "GameBoardComponent").IsUnique().Should().BeTrue();
        }

        [Fact]
        public void DontGenerateDisablesIndexAndMethods()
        {
            const string src = @"
[Entitas.CodeGeneration.Attributes.DontGenerate]
public class FlagComponent : Entitas.IComponent { }
";
            var c = Component(Discover(Game, src), "FlagComponent");
            c.ShouldGenerateIndex().Should().BeFalse();
            c.ShouldGenerateMethods().Should().BeFalse();
        }

        [Fact]
        public void EventAttributeSynthesizesListenerComponent()
        {
            const string src = @"
[Entitas.CodeGeneration.Attributes.Event(Entitas.CodeGeneration.Attributes.EventTarget.Self)]
public class PositionComponent : Entitas.IComponent { public int value; }
";
            var result = Discover(Game, src);
            Component(result, "PositionComponent").IsEvent().Should().BeTrue();

            // A listener component is synthesized for the single (Game) context.
            result.Components.Should().Contain(c => c.GetTypeName() == "PositionListenerComponent");
        }

        [Fact]
        public void WatchedAttributeSynthesizesChangedComponent()
        {
            const string src = @"
[Entitas.CodeGeneration.Attributes.Watched]
public class HealthComponent : Entitas.IComponent { public int value; }
";
            var result = Discover(Game, src);
            Component(result, "HealthComponent").ShouldWatchChanges().Should().BeTrue();
            result.Components.Should().Contain(c => c.GetTypeName() == "HealthChanged");

            result.WatchedCleanups.Should().Contain(w => w.componentData.GetTypeName() == "HealthComponent");
        }

        [Fact]
        public void IgnoreNamespacesChangesComponentName()
        {
            const string src = @"
namespace MyGame { public class HealthComponent : Entitas.IComponent { public int value; } }
";
            // ignoreNamespaces = false -> dots removed, namespace folded into the name.
            var withNs = Discover(Game, src, ignoreNamespaces: false);
            Component(withNs, "MyGame.HealthComponent").ComponentName().Should().Be("MyGameHealth");

            // ignoreNamespaces = true -> only the short type name is used.
            var ignoreNs = Discover(Game, src, ignoreNamespaces: true);
            Component(ignoreNs, "MyGame.HealthComponent").ComponentName().Should().Be("Health");
        }

        [Fact]
        public void ContextDefinitionListIsReadInOrder()
        {
            const string assemblyAttrs =
                "[assembly: Entitas.CodeGeneration.Attributes.ContextDefinition(\"Game\")]\n" +
                "[assembly: Entitas.CodeGeneration.Attributes.ContextDefinition(\"Input\")]\n" +
                "[assembly: Entitas.CodeGeneration.Attributes.ContextDefinition(\"GameState\")]";
            var result = Discover(assemblyAttrs, string.Empty);
            result.Contexts.Select(c => c.GetContextName())
                .Should().Equal("Game", "Input", "GameState");
        }

        [Fact]
        public void DefaultContextUsedWhenComponentHasNoContextAttribute()
        {
            const string src = @"
public class HealthComponent : Entitas.IComponent { public int value; }
";
            // No context attribute -> first declared context (Game) is the default.
            Component(Discover(GameInput, src), "HealthComponent").GetContextNames().Should().Equal("Game");
        }

        [Fact]
        public void MultiContextViaGeneratedContextAttributes()
        {
            const string src = @"
[Entitas.CodeGeneration.Attributes.Game]
[Entitas.CodeGeneration.Attributes.Input]
public class HealthComponent : Entitas.IComponent { public int value; }
";
            Component(Discover(GameInput, src), "HealthComponent").GetContextNames()
                .Should().Equal("Game", "Input");
        }

        [Fact]
        public void DirectContextAttributeResolvesContextName()
        {
            const string src = @"
[Entitas.CodeGeneration.Attributes.Context(""Input"")]
public class HealthComponent : Entitas.IComponent { public int value; }
";
            Component(Discover(GameInput, src), "HealthComponent").GetContextNames()
                .Should().Equal("Input");
        }

        [Fact]
        public void MemberEntityIndexIsDiscovered()
        {
            // Extend the prelude with the entity-index attributes for this test.
            const string assemblyAttrs = Game;
            const string src = @"
namespace Entitas.CodeGeneration.Attributes
{
    using System;
    public abstract class AbstractEntityIndexAttribute : Attribute { protected AbstractEntityIndexAttribute() { } }
    [AttributeUsage(AttributeTargets.Field | AttributeTargets.Property)]
    public sealed class EntityIndexAttribute : AbstractEntityIndexAttribute { }
}
public class UserIdComponent : Entitas.IComponent
{
    [Entitas.CodeGeneration.Attributes.EntityIndex] public string value;
}
";
            var result = Discover(assemblyAttrs, src);
            var index = result.EntityIndices.Single();
            index.GetEntityIndexType().Should().Be("Entitas.EntityIndex");
            index.GetComponentType().Should().Be("UserIdComponent");
            index.GetMemberName().Should().Be("value");
            index.GetKeyType().Should().Be("string");
            index.IsCustom().Should().BeFalse();
            index.GetContextNames().Should().Equal("Game");
        }

        [Fact]
        public void CleanupComponentIsDiscoveredWithMode()
        {
            const string src = @"
[Entitas.CodeGeneration.Attributes.Cleanup(Entitas.CodeGeneration.Attributes.CleanupMode.DestroyEntity)]
public class DestroyComponent : Entitas.IComponent { }
";
            var result = Discover(Game, src);
            var cleanup = result.Cleanups.Single();
            cleanup.componentData.GetTypeName().Should().Be("DestroyComponent");
            cleanup.cleanupMode.Should().Be(CleanupMode.DestroyEntity);
        }
    }
}
