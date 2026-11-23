using System.Collections.Generic;
using Entitas;

namespace Harbor
{
    /// <summary>Orders a ship in every third tick of the signal clock.</summary>
    public sealed class IssueDockingOrdersSystem : IExecuteSystem
    {
        readonly SignalContext _signal;

        public IssueDockingOrdersSystem(SignalContext signal) => _signal = signal;

        public void Execute()
        {
            if (_signal.hasClock && _signal.clock.Value % 3 == 0)
                Make.Blank().AddDockingOrder(_signal.clock.Value);
        }
    }

    /// <summary>A second reader of every order, ahead of its handler.</summary>
    public sealed class LogOrdersSystem : IExecuteSystem
    {
        readonly IGroup<PortEntity> _orders;
        public int Logged;

        public LogOrdersSystem(PortContext port) => _orders = port.GetGroup(PortMatcher.DockingOrder);

        public void Execute() => Logged += _orders.count;
    }

    /// <summary>The handler: docks the ship an order names, announces it, and is done with the order either way.</summary>
    public sealed class DockShipsSystem : IExecuteSystem
    {
        readonly PortContext _port;
        readonly IGroup<PortEntity> _orders;
        readonly List<PortEntity> _buffer = new List<PortEntity>();

        public DockShipsSystem(PortContext port)
        {
            _port = port;
            _orders = port.GetGroup(PortMatcher.AllOf(PortMatcher.DockingOrder));
        }

        public void Execute()
        {
            if (_orders.count == 0)
                return;

            foreach (PortEntity order in _orders.GetEntities(_buffer))
            {
                PortEntity ship = _port.GetEntityWithShipId(order.dockingOrder.Value);
                if (ship == null)
                {
                    order.Destroy();
                    continue;
                }

                ship.isDocked = true;
                Make.Pulse().AddShipDocked(order.dockingOrder.Value);
                order.Destroy();
            }
        }
    }

    /// <summary>Refuels the ship an order names and marks the order for the doom sweeper instead of destroying it.</summary>
    public sealed class RefuelShipsSystem : IExecuteSystem
    {
        readonly PortContext _port;
        readonly IGroup<PortEntity> _orders;
        readonly List<PortEntity> _buffer = new List<PortEntity>();

        public RefuelShipsSystem(PortContext port)
        {
            _port = port;
            _orders = port.GetGroup(PortMatcher.RefuelOrder);
        }

        public void Execute()
        {
            foreach (PortEntity order in _orders.GetEntities(_buffer))
            {
                PortEntity ship = _port.GetEntityWithShipId(order.refuelOrder.Value);
                if (ship != null)
                    ship.ReplaceFuel(100);
                order.isDoomed = true;
            }
        }
    }

    /// <summary>Sells a ship: the ship is destroyed by its id, not from a loop over ships — it is no message.</summary>
    public sealed class SellShipsSystem : IExecuteSystem
    {
        readonly PortContext _port;
        readonly IGroup<PortEntity> _orders;
        readonly List<PortEntity> _buffer = new List<PortEntity>();

        public SellShipsSystem(PortContext port)
        {
            _port = port;
            _orders = port.GetGroup(PortMatcher.SaleOrder);
        }

        public void Execute()
        {
            foreach (PortEntity order in _orders.GetEntities(_buffer))
            {
                PortEntity ship = _port.GetEntityWithShipId(order.saleOrder.Value);
                if (ship != null)
                    ship.Destroy();
                order.Destroy();
            }
        }
    }
}
