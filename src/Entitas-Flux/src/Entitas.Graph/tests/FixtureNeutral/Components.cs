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

    // In the yard. An order put on a ship that is already there is taken off once handled, the
    // ship lives on; one handled only once the clock runs is a message by name alone.
    [Port] public sealed class RepaintOrder : IComponent { public int Value; }  // RepaintHullsSystem paints the hull and takes it off
    [Port] public sealed class Paint : IComponent { public int Value; }
    [Port] public sealed class TowOrder : IComponent { public int Value; }      // TowHullsSystem destroys it, but not before the clock runs

    // Taken off ships, and no one's request: cleared and nothing else, cleared with only a "done"
    // flag set, taken down at the end of the frame, struck because Priority went, or set and
    // taken off by the same system.
    [Port] public sealed class Hailed : IComponent { }
    [Port] public sealed class Sighted : IComponent { }
    [Port] public sealed class Logged : IComponent { }
    [Port] public sealed class Salute : IComponent { }
    [Port] public sealed class Colours : IComponent { }
    [Port] public sealed class SurveyDue : IComponent { }

    // A pilot is sent for a ship: the order goes at the end of every pass, also when a jump skips
    // the work. A beacon is lit every frame, watched and doused at its end: no one's request.
    [Port] public sealed class PilotOrder : IComponent { public int Value; }  // PilotShipsSystem: `goto done;` lands on its Destroy()
    [Port] public sealed class Beacon : IComponent { public int Value; }      // DouseBeaconsSystem sweeps it in Cleanup, nothing else

    // Probes, deployed with the asset of their hologram: the projector mounts the Hologram once
    // the asset has loaded, after an await; a Charge comes from a system of its own. Each due
    // flag is an order its handler takes off, and each handler needs one of the two.
    [Port] public sealed class Probe : IComponent { }
    [Port] public sealed class HoloAsset : IComponent { public string Value; }
    [Port] public sealed class Hologram : IComponent { public object Value; }
    [Port] public sealed class Charge : IComponent { public int Value; }
    [Port] public sealed class LaunchDue : IComponent { }   // LaunchProbesSystem needs Hologram
    [Port] public sealed class PingDue : IComponent { }     // PingProbesSystem needs Charge
    [Port] public sealed class Telemetry : IComponent { public int Value; }
    [Port] public sealed class BeamOrder : IComponent { public int Value; }
    [Port] public sealed class Lens : IComponent { public int Value; }
    [Port] public sealed class CustomsCheck : IComponent { public int Value; }  // ClearCustomsSystem seals every check, then destroys the sealed ones
    [Port] public sealed class Seal : IComponent { public int Value; }       // LensFitting, through IFitting and the abstract Fitting, after the await  // BeamToShipsSystem destroys it only once the ship has its Hologram

    // Moorings. A craft rides on its Hawser; the gantry holding it has its grapples latch on, and
    // the magnetic one puts a Lodestone on whatever craft its gantry holds. Unmooring lets the
    // gantry go — each grapple unlatches, through IGrapple and the abstract Grapple, and the
    // magnetic one takes the Lodestone off again — darkens the slipway left behind and casts the
    // Hawser off. Slackening a hawser pulled too far snaps the Spool off instead of paying out.
    [Port, Watched] public sealed class Hawser : IComponent { public int Value; }
    [Port] public sealed class GantryLink : IComponent { public Gantry Value; }
    [Port] public sealed class Lodestone : IComponent { public int Value; }   // AlignLodestonesSystem needs it; unmooring takes it off through a grapple
    [Port] public sealed class Spool : IComponent { public int Value; }       // ReelSpoolsSystem needs it; unmooring leaves it, slackening takes it off and returns
    [Port] public sealed class Floodlight : IComponent { public int Value; }  // DimFloodlightsSystem needs it; unmooring takes it off the slipway, not the craft
    [Port] public sealed class Slipway : IComponent { public int Value; }
}
