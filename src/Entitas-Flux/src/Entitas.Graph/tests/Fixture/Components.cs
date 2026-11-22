using Entitas;
using Entitas.CodeGeneration.Attributes;

namespace Sample
{
    // Event plumbing, as most Entitas projects do it: every event carries Event, gets Ready
    // after one frame, and one generic system destroys Event + Ready.
    [Game] public sealed class Event : IComponent { }
    [Game] public sealed class Ready : IComponent { }
    [Game] public sealed class Destructed : IComponent { }

    [Game] public sealed class Furniture : IComponent { }
    [Game] public sealed class Id : IComponent { [PrimaryEntityIndex] public int Value; }
    [Game] public sealed class Health : IComponent { public int Value; }
    [Game] public sealed class PlaceFurnitureRequest : IComponent { public int Value; }
    [Game] public sealed class FurniturePlacedEvent : IComponent { public int Value; }
    [Game, Watched] public sealed class Selected : IComponent { }
    [Game, Cleanup(CleanupMode.RemoveComponent)] public sealed class Tapped : IComponent { }
    [Input, Unique] public sealed class Pointer : IComponent { public int Value; }

    // Findings the report must list.
    [Game] public sealed class Dead : IComponent { }              // read, never created
    [Game] public sealed class Unused : IComponent { }            // created, never read
    [Game] public sealed class OrphanRequest : IComponent { }     // a request no system handles
    [Game] public sealed class Inspected : IComponent { }         // state flag set on existing entities, never cleared
}
