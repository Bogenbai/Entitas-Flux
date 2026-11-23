using Entitas;

namespace Harbor
{
    /// <summary>Unmoors every craft whose hawser is slack, leaving the lit slipway.</summary>
    public sealed class UnmoorCraftsSystem : IExecuteSystem
    {
        readonly IGroup<PortEntity> _moored;
        readonly IGroup<PortEntity> _slipways;

        public UnmoorCraftsSystem(PortContext port)
        {
            _moored = port.GetGroup(PortMatcher.AllOf(PortMatcher.Hawser, PortMatcher.DerrickLink));
            _slipways = port.GetGroup(PortMatcher.AllOf(PortMatcher.Slipway, PortMatcher.Floodlight));
        }

        public void Execute()
        {
            foreach (PortEntity craft in _moored.GetEntities())
            {
                if (craft.hawser.Value <= 0)
                    craft.Unmoor(_slipways.GetSingleEntity());
            }
        }
    }

    /// <summary>Pays out every hawser a little.</summary>
    public sealed class SlackenHawsersSystem : IExecuteSystem
    {
        readonly IGroup<PortEntity> _moored;

        public SlackenHawsersSystem(PortContext port) => _moored = port.GetGroup(PortMatcher.AllOf(PortMatcher.Hawser, PortMatcher.Spool));

        public void Execute()
        {
            foreach (PortEntity craft in _moored.GetEntities())
                craft.Slacken();
        }
    }

    /// <summary>Reacts to hawser changes on a craft with a lodestone: the one unmooring takes off.</summary>
    public sealed class AlignLodestonesSystem : IExecuteSystem
    {
        readonly IGroup<PortEntity> _changed;
        public int Aligned;

        public AlignLodestonesSystem(PortContext port) =>
            _changed = port.GetGroup(PortMatcher.AllOf(PortMatcher.Lodestone).AnyOf(PortMatcher.HawserChanged));

        public void Execute()
        {
            foreach (PortEntity craft in _changed)
                Aligned += craft.lodestone.Value;
        }
    }

    /// <summary>Reacts to hawser changes on a craft with a spool, which unmooring leaves.</summary>
    public sealed class ReelSpoolsSystem : IExecuteSystem
    {
        readonly IGroup<PortEntity> _changed;
        public int Reeled;

        public ReelSpoolsSystem(PortContext port) =>
            _changed = port.GetGroup(PortMatcher.AllOf(PortMatcher.Spool).AnyOf(PortMatcher.HawserChanged));

        public void Execute()
        {
            foreach (PortEntity craft in _changed)
                Reeled += craft.spool.Value;
        }
    }

    /// <summary>Reacts to hawser changes on a lit craft: unmooring darkens the slipway, not the craft.</summary>
    public sealed class DimFloodlightsSystem : IExecuteSystem
    {
        readonly IGroup<PortEntity> _changed;
        public int Dimmed;

        public DimFloodlightsSystem(PortContext port) =>
            _changed = port.GetGroup(PortMatcher.AllOf(PortMatcher.Floodlight).AnyOf(PortMatcher.HawserChanged));

        public void Execute()
        {
            foreach (PortEntity craft in _changed)
                Dimmed += craft.floodlight.Value;
        }
    }
}
