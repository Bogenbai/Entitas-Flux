using System.Collections.Generic;
using Entitas;

namespace Harbor
{
    public sealed class TickClockSystem : IExecuteSystem
    {
        readonly SignalContext _signal;

        public TickClockSystem(SignalContext signal) => _signal = signal;

        public void Execute() => _signal.ReplaceClock(_signal.hasClock ? _signal.clock.Value + 1 : 0);
    }

    /// <summary>Launches a ship every tenth tick, through the factory.</summary>
    public sealed class LaunchShipsSystem : IExecuteSystem
    {
        readonly SignalContext _signal;
        readonly ShipFactory _factory;

        public LaunchShipsSystem(SignalContext signal, ShipFactory factory)
        {
            _signal = signal;
            _factory = factory;
        }

        public void Execute()
        {
            if (_signal.hasClock && _signal.clock.Value % 10 == 0)
                _factory.Launch(_signal.clock.Value);
        }
    }

    /// <summary>Sees each ship once: its group requires Fresh, which no ship keeps.</summary>
    public sealed class RegisterShipsSystem : IExecuteSystem
    {
        readonly IGroup<PortEntity> _fresh;

        public RegisterShipsSystem(PortContext port) => _fresh = port.GetGroup(PortMatcher.AllOf(PortMatcher.Ship, PortMatcher.Fresh));

        public void Execute()
        {
            foreach (PortEntity ship in _fresh.GetEntities())
                ship.isRegistered = true;
        }
    }

    /// <summary>Takes Fresh off every ship holding it.</summary>
    public sealed class ForgetFreshSystem : ICleanupSystem
    {
        readonly IGroup<PortEntity> _fresh;
        readonly List<PortEntity> _buffer = new List<PortEntity>();

        public ForgetFreshSystem(PortContext port) => _fresh = port.GetGroup(PortMatcher.Fresh);

        public void Cleanup()
        {
            foreach (PortEntity entity in _fresh.GetEntities(_buffer))
                entity.isFresh = false;
        }
    }

    /// <summary>Registered ships at sea burn fuel; Fuel is [Watched].</summary>
    public sealed class BurnFuelSystem : IExecuteSystem
    {
        readonly IGroup<PortEntity> _atSea;

        public BurnFuelSystem(PortContext port)
        {
            _atSea = port.GetGroup(PortMatcher
                .AllOf(PortMatcher.Ship, PortMatcher.Fuel, PortMatcher.Registered)
                .NoneOf(PortMatcher.Docked));
        }

        public void Execute()
        {
            foreach (PortEntity ship in _atSea.GetEntities())
                ship.ReplaceFuel(ship.fuel.Value - 1);
        }
    }

    /// <summary>Reacts to fuel changes: a ship running low orders a refuel.</summary>
    public sealed class WarnLowFuelSystem : IExecuteSystem
    {
        readonly IGroup<PortEntity> _changed;

        public WarnLowFuelSystem(PortContext port) => _changed = port.GetGroup(PortMatcher.AllOf(PortMatcher.Ship, PortMatcher.FuelChanged));

        public void Execute()
        {
            foreach (PortEntity ship in _changed.GetEntities())
            {
                if (ship.hasFuel && ship.fuel.Value < 20)
                    Make.Blank().AddRefuelOrder(ship.shipId.Value);
            }
        }
    }

    /// <summary>Flags ships through helpers and a private method, each under a condition of its own.</summary>
    public sealed class PrioritizeShipsSystem : IExecuteSystem
    {
        readonly IGroup<PortEntity> _ships;

        public PrioritizeShipsSystem(PortContext port) => _ships = port.GetGroup(PortMatcher.AllOf(PortMatcher.Ship, PortMatcher.Cargo, PortMatcher.Fuel));

        public void Execute()
        {
            foreach (PortEntity ship in _ships.GetEntities())
            {
                ship.Apply(x => x.isPriority = true, gate: ship.cargo.Value > 100)
                    .Unless(x => x.isInspected = true, skip: ship.isPriority);
                if (ship.fuel.Value < 10)
                    Expedite(ship);
            }
        }

        void Expedite(PortEntity ship) => ship.isExpedited = true;
    }

    /// <summary>
    /// A ship is doomed only once its hull is gone: long-lived, no message. Wrecked, set in the
    /// same loop, goes with it.
    /// </summary>
    public sealed class ScrapShipsSystem : IExecuteSystem
    {
        readonly IGroup<PortEntity> _ships;
        readonly List<PortEntity> _buffer = new List<PortEntity>();

        public ScrapShipsSystem(PortContext port) => _ships = port.GetGroup(PortMatcher.AllOf(PortMatcher.Ship, PortMatcher.Hull));

        public void Execute()
        {
            foreach (PortEntity ship in _ships.GetEntities(_buffer))
            {
                if (ship.hull.Value <= 0)
                {
                    ship.isWrecked = true;
                    ship.isDoomed = true;
                }
            }
        }
    }

    /// <summary>Dooms every ship, but only while a scrap order is there: the ships are no message, the order is.</summary>
    public sealed class ScrapFleetSystem : IExecuteSystem
    {
        readonly IGroup<PortEntity> _orders;
        readonly IGroup<PortEntity> _ships;
        readonly List<PortEntity> _buffer = new List<PortEntity>();

        public ScrapFleetSystem(PortContext port)
        {
            _orders = port.GetGroup(PortMatcher.ScrapFleetOrder);
            _ships = port.GetGroup(PortMatcher.Ship);
        }

        public void Execute()
        {
            foreach (PortEntity order in _orders.GetEntities(_buffer))
            {
                foreach (PortEntity ship in _ships.GetEntities())
                    ship.isDoomed = true;
                order.Destroy();
            }
        }
    }
}
