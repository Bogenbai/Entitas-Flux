using Entitas;
using Entitas.CodeGeneration.Attributes;

namespace Harbor
{
    // On the quay, where the jumps that skip a write sit inside an if rather than next to it.
    // Calls name neither `Order` nor `Request`: the suffix tests leave them alone.
    [Port] public sealed class BunkerCall : IComponent { public int Value; }   // BunkerHullsSystem skips a wrecked ship's call: it waits
    [Port] public sealed class GangwayCall : IComponent { public int Value; }  // LowerGangwaysSystem turns a call down or answers it: never waits
    [Port] public sealed class KeelCall : IComponent { public int Value; }     // HaulKeelsSystem destroys it only on some ticks
    [Port] public sealed class Ballast : IComponent { public int Value; }
    [Port] public sealed class Mooring : IComponent { public int Value; }
    [Port] public sealed class Cable : IComponent { public int Value; }
    [Port] public sealed class Keel : IComponent { public int Value; }
    [Port] public sealed class Bilge : IComponent { public int Value; }
    [Port] public sealed class Bollard : IComponent { public int Value; }

    /// <summary>The quay's panel: where the calls come from.</summary>
    public sealed class QuayPanel
    {
        public void OnBunkerPressed(int shipId) => Make.Blank().AddBunkerCall(shipId);

        public void OnGangwayPressed(int shipId) => Make.Blank().AddGangwayCall(shipId);

        public void OnHaulPressed(int tons) => Make.Blank().AddKeelCall(tons);

        public void OnMoorPressed(int lines) => Make.Blank().AddMooring(lines);
    }

    /// <summary>
    /// Makes a mooring fast with a bollard, unless it already has a cable and is to stay slack.
    /// It counts the moorings with lines first: the jump is not one the method starts with.
    /// </summary>
    public static class Quays
    {
        static int _lined;

        public static void Secure(PortEntity mooring, bool slack)
        {
            if (mooring.mooring.Value > 0)
                _lined++;
            if (mooring.hasCable)
            {
                if (slack)
                    return;
            }
            mooring.ReplaceBollard(1);
        }
    }

    /// <summary>
    /// Bunkers the ship a call names, but not a wrecked one: the `continue` sits in the if that
    /// found the ship, and the call stays until the ship is gone or no longer wrecked.
    /// </summary>
    public sealed class BunkerHullsSystem : IExecuteSystem
    {
        readonly PortContext _port;
        readonly IGroup<PortEntity> _calls;

        public BunkerHullsSystem(PortContext port)
        {
            _port = port;
            _calls = port.GetGroup(PortMatcher.BunkerCall);
        }

        public void Execute()
        {
            foreach (PortEntity call in _calls.GetEntities())
            {
                PortEntity ship = _port.GetEntityWithShipId(call.bunkerCall.Value);
                if (ship != null)
                {
                    if (ship.isWrecked)
                        continue;
                    ship.ReplaceBallast(call.bunkerCall.Value);
                }
                call.Destroy();
            }
        }
    }

    /// <summary>
    /// Lowers the gangway of a docked ship and turns the call down for one that is not: both ways
    /// destroy it, the one on the way to the nested `continue` and the one after it.
    /// </summary>
    public sealed class LowerGangwaysSystem : IExecuteSystem
    {
        readonly PortContext _port;
        readonly IGroup<PortEntity> _calls;

        public LowerGangwaysSystem(PortContext port)
        {
            _port = port;
            _calls = port.GetGroup(PortMatcher.GangwayCall);
        }

        public void Execute()
        {
            foreach (PortEntity call in _calls.GetEntities())
            {
                PortEntity ship = _port.GetEntityWithShipId(call.gangwayCall.Value);
                if (ship != null)
                {
                    if (!ship.isDocked)
                    {
                        call.Destroy();
                        continue;
                    }
                    ship.ReplaceBallast(1);
                }
                call.Destroy();
            }
        }
    }

    /// <summary>
    /// Jumps that say nothing about what follows them, or say it only partly: one that leaves a
    /// loop of its own, ones under a switch or a try, one in an else, one behind a goto that
    /// lands before the write.
    /// </summary>
    public sealed class MoorHullsSystem : IExecuteSystem
    {
        readonly IGroup<PortEntity> _moorings;
        int _turns;

        public MoorHullsSystem(PortContext port) => _moorings = port.GetGroup(PortMatcher.Mooring);

        public void Execute()
        {
            // The `continue` leaves the loop over the bollards, not the mooring's: every mooring gets its cable.
            foreach (PortEntity mooring in _moorings.GetEntities())
            {
                var lines = 0;
                if (mooring.mooring.Value > 0)
                {
                    for (var bollard = 0; bollard < mooring.mooring.Value; bollard++)
                    {
                        if (bollard % 2 == 1)
                            continue;
                        lines++;
                    }
                }
                mooring.ReplaceCable(lines);
                Quays.Secure(mooring, slack: false);
            }

            // Under a switch and under a try: too involved to read, left as they were.
            foreach (PortEntity mooring in _moorings.GetEntities())
            {
                switch (mooring.mooring.Value)
                {
                    case 0:
                        if (mooring.hasCable)
                            continue;
                        break;
                }
                try
                {
                    if (mooring.hasBilge)
                    {
                        if (mooring.bilge.Value > 9)
                            continue;
                    }
                }
                finally
                {
                    _turns++;
                }
                mooring.ReplaceKeel(_turns);
            }

            // In an else: the bilge is pumped unless the mooring has no cable and has a bilge.
            foreach (PortEntity mooring in _moorings.GetEntities())
            {
                if (mooring.hasCable)
                {
                    _turns++;
                }
                else
                {
                    if (mooring.hasBilge)
                        continue;
                }
                mooring.ReplaceBilge(0);
            }

            // The goto lands before the write, so the `continue` after it says nothing about it.
            foreach (PortEntity mooring in _moorings.GetEntities())
            {
                if (mooring.hasKeel)
                {
                    if (mooring.keel.Value > 1)
                        goto stow;
                    if (mooring.keel.Value < 0)
                        continue;
                }
                stow:
                mooring.ReplaceBallast(mooring.mooring.Value);
            }
        }
    }

    /// <summary>Leaves every mooring slack: Quays.Secure skips those with a cable.</summary>
    public sealed class SlackenMooringsSystem : IExecuteSystem
    {
        readonly IGroup<PortEntity> _moorings;

        public SlackenMooringsSystem(PortContext port) => _moorings = port.GetGroup(PortMatcher.Mooring);

        public void Execute()
        {
            foreach (PortEntity mooring in _moorings.GetEntities())
                Quays.Secure(mooring, slack: true);
        }
    }

    /// <summary>
    /// Hauls keels on the clock's odd ticks: the jump nested at the top of Execute says when the
    /// system runs at all, not which of its writes happen — and the calls may wait for it.
    /// </summary>
    public sealed class HaulKeelsSystem : IExecuteSystem
    {
        readonly SignalContext _signal;
        readonly IGroup<PortEntity> _calls;
        int _hauled;

        public HaulKeelsSystem(SignalContext signal, PortContext port)
        {
            _signal = signal;
            _calls = port.GetGroup(PortMatcher.KeelCall);
        }

        public void Execute()
        {
            if (_signal.hasClock)
            {
                if (_signal.clock.Value % 2 == 0)
                    return;
            }

            foreach (PortEntity call in _calls.GetEntities())
            {
                _hauled += call.keelCall.Value;
                call.Destroy();
            }
        }
    }
}
