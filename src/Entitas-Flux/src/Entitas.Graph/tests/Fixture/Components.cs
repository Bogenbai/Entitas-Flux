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

    // A shop: items are bought unpaid and paid on commit. Unpaid is [Watched], so the systems
    // reacting to the payment hang off the system that removes it.
    [Game] public sealed class ShopItem : IComponent { }
    [Game, Watched] public sealed class Unpaid : IComponent { }
    [Game] public sealed class ItemPurchasedEvent : IComponent { public int Value; }

    // Flags that look never cleared and are not, or are on purpose.
    [Game] public sealed class ProcessingStoring : IComponent { }  // set in the loop that marks the entity Destructed
    [Game] public sealed class TimeIsUp : IComponent { }           // required by a group whose entities a helper marks Destructed
    [Game] public sealed class Unboxed : IComponent { }            // a done marker: its setter filters it out with NoneOf
    [Game] public sealed class Receipted : IComponent { }          // the same guard, but set in reaction to a [Watched] change

    // Crates, spawned with markers that do not stay: what a flag's setter requires decides
    // whether the flag is set once.
    [Game] public sealed class Crate : IComponent { }
    [Game] public sealed class JustSpawned : IComponent { }        // transient: one system takes it off every crate holding it
    [Game] public sealed class SpawnSeen : IComponent { }          // set by that system, which leaves it out with NoneOf
    [Game] public sealed class Wrapped : IComponent { }            // transient too, taken off with RemoveComponent(index)
    [Game] public sealed class Fragile : IComponent { }            // taken off some crates only: not transient
    [Game] public sealed class Stacked : IComponent { }            // taken off unwrapped crates only (NoneOf): not transient
    [Game] public sealed class CrateRestored : IComponent { }      // set once on purpose: its setter only sees JustSpawned crates
    [Game] public sealed class CrateLabelled : IComponent { }      // set on every crate, every frame: never cleared

    // Findings the report must list.
    [Game] public sealed class Dead : IComponent { }              // read, never created
    [Game] public sealed class Unused : IComponent { }            // created, never read
    [Game] public sealed class OrphanRequest : IComponent { }     // a request no system handles
    [Game] public sealed class Inspected : IComponent { }         // state flag set on existing entities, never cleared
}
