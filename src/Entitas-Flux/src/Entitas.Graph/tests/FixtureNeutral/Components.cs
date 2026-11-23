using Entitas;
using Entitas.CodeGeneration.Attributes;

namespace Harbor
{
    // Messages, named for what they say, not for being messages.
    [Port] public sealed class DockingOrder : IComponent { public int Value; }   // handled by DockShipsSystem, which destroys it
    [Port] public sealed class RefuelOrder : IComponent { public int Value; }    // handled by RefuelShipsSystem, which marks it Doomed
    [Port] public sealed class SaleOrder : IComponent { public int Value; }      // handled by SellShipsSystem, which destroys it
    [Port] public sealed class ScrapFleetOrder : IComponent { }                  // handled by ScrapFleetSystem, which destroys it
    [Port] public sealed class ShipDocked : IComponent { public int Value; }     // made with Make.Pulse()
    [Port] public sealed class CargoUnloaded : IComponent { public int Value; }  // made with Make.Pulse()

    // Plumbing: every pulse is armed a frame later and swept once armed; whatever is doomed is swept.
    [Port] public sealed class Pulse : IComponent { }
    [Port] public sealed class Armed : IComponent { }
    [Port] public sealed class Doomed : IComponent { }

    // Ships live long: scrapped only once the hull is gone, sold by id, doomed with the fleet on order.
    [Port] public sealed class Ship : IComponent { }
    [Port] public sealed class ShipId : IComponent { [PrimaryEntityIndex] public int Value; }
    [Port, Watched] public sealed class Fuel : IComponent { public int Value; }
    [Port] public sealed class Cargo : IComponent { public int Value; }
    [Port] public sealed class Hull : IComponent { public int Value; }
    [Port] public sealed class Docked : IComponent { }
    [Port] public sealed class Wrecked : IComponent { }

    // Only tankers carry one: the factory adds it in `case ShipKind.Tanker`, and each launcher passes its kind as a constant.
    [Port] public sealed class Tank : IComponent { public int Value; }
    public enum ShipKind { Freighter, Tanker }

    // A one-frame marker, and a flag set once while a ship still carries it.
    [Port] public sealed class Fresh : IComponent { }
    [Port] public sealed class Registered : IComponent { }

    // Set through helpers and a private method, each under a condition.
    [Port] public sealed class Priority : IComponent { }
    [Port] public sealed class Inspected : IComponent { }
    [Port] public sealed class Expedited : IComponent { }

    // Fuel depots: built by a factory, never a ship; a gauge is fitted later, not built in.
    [Port] public sealed class Depot : IComponent { }
    [Port, Watched] public sealed class Gauge : IComponent { public int Value; }

    [Signal, Unique] public sealed class Clock : IComponent { public int Value; }
}
