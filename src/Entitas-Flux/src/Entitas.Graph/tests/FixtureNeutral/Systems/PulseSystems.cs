using System.Collections.Generic;
using Entitas;

namespace Harbor
{
    /// <summary>Arms every pulse once: a pulse made this frame is read armed the next.</summary>
    public sealed class ArmPulsesSystem : IExecuteSystem
    {
        readonly IGroup<PortEntity> _pulses;
        readonly List<PortEntity> _buffer = new List<PortEntity>();

        public ArmPulsesSystem(PortContext port)
        {
            _pulses = port.GetGroup(PortMatcher
                .AllOf(PortMatcher.Pulse)
                .NoneOf(PortMatcher.Armed));
        }

        public void Execute()
        {
            foreach (PortEntity pulse in _pulses.GetEntities(_buffer))
                pulse.isArmed = true;
        }
    }

    /// <summary>The generic sweeper: every armed pulse goes, whatever it carries.</summary>
    public sealed class SweepPulsesSystem : IExecuteSystem
    {
        readonly IGroup<PortEntity> _armed;
        readonly List<PortEntity> _buffer = new List<PortEntity>();

        public SweepPulsesSystem(PortContext port) => _armed = port.GetGroup(PortMatcher.AllOf(PortMatcher.Pulse, PortMatcher.Armed));

        public void Execute()
        {
            foreach (PortEntity pulse in _armed.GetEntities(_buffer))
                pulse.Destroy();
        }
    }

    /// <summary>Whatever is marked Doomed goes.</summary>
    public sealed class SweepDoomedSystem : IExecuteSystem
    {
        readonly IGroup<PortEntity> _doomed;
        readonly List<PortEntity> _buffer = new List<PortEntity>();

        public SweepDoomedSystem(PortContext port) => _doomed = port.GetGroup(PortMatcher.Doomed);

        public void Execute()
        {
            foreach (PortEntity entity in _doomed.GetEntities(_buffer))
                entity.Destroy();
        }
    }

    public sealed class GreetCaptainSystem : IExecuteSystem
    {
        readonly IGroup<PortEntity> _docked;
        public int Greeted;

        public GreetCaptainSystem(PortContext port) => _docked = port.GetGroup(PortMatcher.AllOf(PortMatcher.ShipDocked, PortMatcher.Armed));

        public void Execute() => Greeted += _docked.count;
    }

    /// <summary>Unloads a docked ship, and says so with a pulse of its own.</summary>
    public sealed class UnloadCargoSystem : IExecuteSystem
    {
        readonly PortContext _port;
        readonly IGroup<PortEntity> _docked;

        public UnloadCargoSystem(PortContext port)
        {
            _port = port;
            _docked = port.GetGroup(PortMatcher.AllOf(PortMatcher.ShipDocked, PortMatcher.Armed));
        }

        public void Execute()
        {
            foreach (PortEntity docked in _docked.GetEntities())
            {
                PortEntity ship = _port.GetEntityWithShipId(docked.shipDocked.Value);
                if (ship != null && ship.hasCargo)
                {
                    Make.Pulse().AddCargoUnloaded(ship.cargo.Value);
                    ship.ReplaceCargo(0);
                }
            }
        }
    }

    public sealed class CountCargoSystem : IExecuteSystem
    {
        readonly IGroup<PortEntity> _unloaded;
        public int Total;

        public CountCargoSystem(PortContext port) => _unloaded = port.GetGroup(PortMatcher.AllOf(PortMatcher.CargoUnloaded, PortMatcher.Armed));

        public void Execute()
        {
            foreach (PortEntity unloaded in _unloaded)
                Total += unloaded.cargoUnloaded.Value;
        }
    }

    /// <summary>Named after what it reads, and says nothing else: no seed of its own for "ship docked".</summary>
    public sealed class CountShipsDockedSystem : IExecuteSystem
    {
        readonly IGroup<PortEntity> _docked;
        public int Count;

        public CountShipsDockedSystem(PortContext port) => _docked = port.GetGroup(PortMatcher.AllOf(PortMatcher.ShipDocked, PortMatcher.Armed));

        public void Execute() => Count += _docked.count;
    }
}
