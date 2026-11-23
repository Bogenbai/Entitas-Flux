using Entitas;

namespace Harbor
{
    /// <summary>
    /// Shows a depot's gauge when its fuel or the gauge changes. It runs before the depots are
    /// built and leaking, so it never sees those fuel changes; it shows the gauge once fitted.
    /// </summary>
    public sealed class ShowGaugesSystem : IExecuteSystem
    {
        readonly IGroup<PortEntity> _changed;
        public int Shown;

        public ShowGaugesSystem(PortContext port)
        {
            _changed = port.GetGroup(PortMatcher
                .AllOf(PortMatcher.Depot, PortMatcher.Gauge)
                .AnyOf(PortMatcher.FuelChanged, PortMatcher.GaugeChanged));
        }

        public void Execute()
        {
            foreach (PortEntity depot in _changed)
                Shown += depot.gauge.Value;
        }
    }

    /// <summary>Fits a gauge to every depot without one: Gauge arrives after the depot is built.</summary>
    public sealed class FitGaugesSystem : IExecuteSystem
    {
        readonly IGroup<PortEntity> _bare;

        public FitGaugesSystem(PortContext port) => _bare = port.GetGroup(PortMatcher.AllOf(PortMatcher.Depot).NoneOf(PortMatcher.Gauge));

        public void Execute()
        {
            foreach (PortEntity depot in _bare.GetEntities())
                depot.AddGauge(0);
        }
    }

    /// <summary>Builds a depot every twentieth tick, through the factory.</summary>
    public sealed class BuildDepotsSystem : IExecuteSystem
    {
        readonly SignalContext _signal;
        readonly DepotFactory _factory;

        public BuildDepotsSystem(SignalContext signal, DepotFactory factory)
        {
            _signal = signal;
            _factory = factory;
        }

        public void Execute()
        {
            if (_signal.hasClock && _signal.clock.Value % 20 == 0)
                _factory.Build(500);
        }
    }

    /// <summary>Every depot leaks: a fuel change on depots, which no ship-only reaction can take.</summary>
    public sealed class LeakFuelSystem : IExecuteSystem
    {
        readonly IGroup<PortEntity> _depots;

        public LeakFuelSystem(PortContext port) => _depots = port.GetGroup(PortMatcher.AllOf(PortMatcher.Depot, PortMatcher.Fuel));

        public void Execute()
        {
            foreach (PortEntity depot in _depots.GetEntities())
                depot.ReplaceFuel(depot.fuel.Value - 1);
        }
    }
}
