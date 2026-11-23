using System.Collections.Generic;
using Entitas;

namespace Harbor
{
    /// <summary>The handler of an order put on a ship: has the hull painted, then takes the order off. The ship stays.</summary>
    public sealed class RepaintHullsSystem : IExecuteSystem
    {
        readonly IGroup<PortEntity> _ordered;
        readonly List<PortEntity> _buffer = new List<PortEntity>();

        public RepaintHullsSystem(PortContext port) => _ordered = port.GetGroup(PortMatcher.AllOf(PortMatcher.Ship, PortMatcher.RepaintOrder));

        public void Execute()
        {
            foreach (PortEntity ship in _ordered.GetEntities(_buffer))
            {
                Brushes.Coat(ship, ship.repaintOrder.Value);
                ship.RemoveRepaintOrder();
            }
        }
    }

    /// <summary>Tows the ship an order names, once the clock runs: until then every order waits, so it is destroyed only sometimes.</summary>
    public sealed class TowHullsSystem : IExecuteSystem
    {
        readonly SignalContext _signal;
        readonly PortContext _port;
        readonly IGroup<PortEntity> _orders;
        readonly List<PortEntity> _buffer = new List<PortEntity>();

        public TowHullsSystem(SignalContext signal, PortContext port)
        {
            _signal = signal;
            _port = port;
            _orders = port.GetGroup(PortMatcher.TowOrder);
        }

        public void Execute()
        {
            if (!_signal.hasClock)
                return;

            foreach (PortEntity order in _orders.GetEntities(_buffer))
            {
                PortEntity ship = _port.GetEntityWithShipId(order.towOrder.Value);
                if (ship != null && ship.hasHull)
                    ship.ReplaceHull(ship.hull.Value - 1);
                order.Destroy();
            }
        }
    }

    /// <summary>Answers every hail by taking it down, and does nothing else.</summary>
    public sealed class AnswerHailsSystem : IExecuteSystem
    {
        readonly IGroup<PortEntity> _hailed;
        readonly List<PortEntity> _buffer = new List<PortEntity>();

        public AnswerHailsSystem(PortContext port) => _hailed = port.GetGroup(PortMatcher.AllOf(PortMatcher.Ship, PortMatcher.Hailed));

        public void Execute()
        {
            foreach (PortEntity ship in _hailed.GetEntities(_buffer))
                ship.isHailed = false;
        }
    }

    /// <summary>Takes a sighting off and logs the ship, which its group then skips: the "done" flag is all it sets.</summary>
    public sealed class LogSightingsSystem : IExecuteSystem
    {
        readonly IGroup<PortEntity> _sighted;
        readonly List<PortEntity> _buffer = new List<PortEntity>();

        public LogSightingsSystem(PortContext port)
        {
            _sighted = port.GetGroup(PortMatcher
                .AllOf(PortMatcher.Ship, PortMatcher.Sighted)
                .NoneOf(PortMatcher.Logged));
        }

        public void Execute()
        {
            foreach (PortEntity ship in _sighted.GetEntities(_buffer))
            {
                ship.isSighted = false;
                ship.isLogged = true;
            }
        }
    }

    /// <summary>Hoists colours on every ship with priority.</summary>
    public sealed class HoistColoursSystem : IExecuteSystem
    {
        readonly IGroup<PortEntity> _priority;
        readonly List<PortEntity> _buffer = new List<PortEntity>();

        public HoistColoursSystem(PortContext port)
        {
            _priority = port.GetGroup(PortMatcher
                .AllOf(PortMatcher.Ship, PortMatcher.Priority)
                .NoneOf(PortMatcher.Colours));
        }

        public void Execute()
        {
            foreach (PortEntity ship in _priority.GetEntities(_buffer))
                ship.isColours = true;
        }
    }

    /// <summary>Strikes the colours of a ship without priority, and repaints its hull: Colours follows Priority.</summary>
    public sealed class StrikeColoursSystem : IExecuteSystem
    {
        readonly IGroup<PortEntity> _flying;
        readonly List<PortEntity> _buffer = new List<PortEntity>();

        public StrikeColoursSystem(PortContext port)
        {
            _flying = port.GetGroup(PortMatcher
                .AllOf(PortMatcher.Ship, PortMatcher.Colours)
                .NoneOf(PortMatcher.Priority));
        }

        public void Execute()
        {
            foreach (PortEntity ship in _flying.GetEntities(_buffer))
            {
                ship.ReplacePaint(0);
                ship.isColours = false;
            }
        }
    }

    /// <summary>Marks worn hulls for a survey and surveys them in a second loop: its own bookkeeping, handed to no one.</summary>
    public sealed class SurveyHullsSystem : IExecuteSystem
    {
        readonly IGroup<PortEntity> _hulls;
        readonly IGroup<PortEntity> _due;
        readonly List<PortEntity> _buffer = new List<PortEntity>();

        public SurveyHullsSystem(PortContext port)
        {
            _hulls = port.GetGroup(PortMatcher.AllOf(PortMatcher.Ship, PortMatcher.Hull));
            _due = port.GetGroup(PortMatcher.AllOf(PortMatcher.Ship, PortMatcher.SurveyDue));
        }

        public void Execute()
        {
            foreach (PortEntity ship in _hulls.GetEntities(_buffer))
            {
                if (ship.hull.Value < 2)
                    ship.isSurveyDue = true;
            }

            foreach (PortEntity ship in _due.GetEntities(_buffer))
            {
                ship.ReplacePaint(2);
                ship.isSurveyDue = false;
            }
        }
    }

    /// <summary>At the end of the frame, takes every salute down and touches up the paint: a salute is up for the whole frame.</summary>
    public sealed class ReturnSalutesSystem : ICleanupSystem
    {
        readonly IGroup<PortEntity> _saluting;
        readonly List<PortEntity> _buffer = new List<PortEntity>();

        public ReturnSalutesSystem(PortContext port) => _saluting = port.GetGroup(PortMatcher.AllOf(PortMatcher.Ship, PortMatcher.Salute));

        public void Cleanup()
        {
            foreach (PortEntity ship in _saluting.GetEntities(_buffer))
            {
                ship.ReplacePaint(1);
                ship.isSalute = false;
            }
        }
    }

    /// <summary>Sends a pilot to the ship an order names. A gone ship jumps straight to `done:`, which destroys the order: every order goes.</summary>
    public sealed class PilotShipsSystem : IExecuteSystem
    {
        readonly PortContext _port;
        readonly IGroup<PortEntity> _orders;
        readonly List<PortEntity> _buffer = new List<PortEntity>();

        public PilotShipsSystem(PortContext port)
        {
            _port = port;
            _orders = port.GetGroup(PortMatcher.PilotOrder);
        }

        public void Execute()
        {
            foreach (PortEntity order in _orders.GetEntities(_buffer))
            {
                PortEntity ship = _port.GetEntityWithShipId(order.pilotOrder.Value);
                if (ship == null || !ship.hasHull)
                    goto done;
                ship.ReplaceHull(ship.hull.Value + 1);
                done:
                order.Destroy();
            }
        }
    }

    /// <summary>Lights the beacon, every frame.</summary>
    public sealed class LightBeaconsSystem : IExecuteSystem
    {
        readonly PortContext _port;

        public LightBeaconsSystem(PortContext port) => _port = port;

        public void Execute() => _port.CreateEntity().AddBeacon(1);
    }

    /// <summary>Counts the beacons it sees this frame.</summary>
    public sealed class WatchBeaconsSystem : IExecuteSystem
    {
        readonly IGroup<PortEntity> _beacons;
        public int Seen;

        public WatchBeaconsSystem(PortContext port) => _beacons = port.GetGroup(PortMatcher.Beacon);

        public void Execute()
        {
            foreach (PortEntity beacon in _beacons)
                Seen += beacon.beacon.Value;
        }
    }

    /// <summary>Douses every beacon at the end of the frame, and does nothing else.</summary>
    public sealed class DouseBeaconsSystem : ICleanupSystem
    {
        readonly IGroup<PortEntity> _beacons;
        readonly List<PortEntity> _buffer = new List<PortEntity>();

        public DouseBeaconsSystem(PortContext port) => _beacons = port.GetGroup(PortMatcher.Beacon);

        public void Cleanup()
        {
            foreach (PortEntity beacon in _beacons.GetEntities(_buffer))
                beacon.Destroy();
        }
    }
}
