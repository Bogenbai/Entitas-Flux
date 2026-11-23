using Entitas;

namespace Harbor
{
    /// <summary>Charges every probe without a charge: a plain system, ahead of the one deploying probes.</summary>
    public sealed class ChargeProbesSystem : IExecuteSystem
    {
        readonly IGroup<PortEntity> _flat;

        public ChargeProbesSystem(PortContext port) => _flat = port.GetGroup(PortMatcher.AllOf(PortMatcher.Probe).NoneOf(PortMatcher.Charge));

        public void Execute()
        {
            foreach (PortEntity probe in _flat.GetEntities())
                probe.AddCharge(100);
        }
    }

    /// <summary>Deploys a probe with its hologram's asset — no Hologram, no Charge yet — due for launch and a ping.</summary>
    public sealed class DeployProbesSystem : IExecuteSystem
    {
        public void Execute()
        {
            PortEntity probe = Make.Blank();
            probe.isProbe = true;
            probe.AddHoloAsset("probe");
            probe.isLaunchDue = true;
            probe.isPingDue = true;
        }
    }

    /// <summary>Deploys a probe with its Hologram already mounted: its launcher sees it by order alone.</summary>
    public sealed class RedeployProbesSystem : IExecuteSystem
    {
        public void Execute()
        {
            PortEntity probe = Make.Blank();
            probe.isProbe = true;
            probe.AddHologram(new object());
            probe.isLaunchDue = true;
        }
    }

    /// <summary>Starts projecting the hologram of every probe without one; the projector mounts it when the asset is there.</summary>
    public sealed class ProjectHologramsSystem : IExecuteSystem
    {
        readonly HoloProjector _projector = new HoloProjector();
        readonly IGroup<PortEntity> _dark;

        public ProjectHologramsSystem(PortContext port) =>
            _dark = port.GetGroup(PortMatcher.AllOf(PortMatcher.HoloAsset).NoneOf(PortMatcher.Hologram));

        public void Execute()
        {
            foreach (PortEntity probe in _dark.GetEntities())
                _ = _projector.ProjectAsync(probe);
        }
    }

    public sealed class LaunchProbesSystem : IExecuteSystem
    {
        readonly IGroup<PortEntity> _due;

        public LaunchProbesSystem(PortContext port) =>
            _due = port.GetGroup(PortMatcher.AllOf(PortMatcher.Probe, PortMatcher.LaunchDue, PortMatcher.Hologram));

        public void Execute()
        {
            foreach (PortEntity probe in _due.GetEntities())
            {
                probe.ReplaceTelemetry(1);
                probe.isLaunchDue = false;
            }
        }
    }

    /// <summary>Reads its order on the entity its loop visits, too: still only through its group.</summary>
    public sealed class PingProbesSystem : IExecuteSystem
    {
        readonly IGroup<PortEntity> _due;

        public PingProbesSystem(PortContext port) =>
            _due = port.GetGroup(PortMatcher.AllOf(PortMatcher.Probe, PortMatcher.PingDue, PortMatcher.Charge));

        public void Execute()
        {
            foreach (PortEntity probe in _due.GetEntities())
            {
                probe.ReplaceTelemetry(probe.isPingDue ? probe.charge.Value : 0);
                probe.isPingDue = false;
            }
        }
    }

    /// <summary>
    /// Beams to the ship an order names, once the ship shows a Hologram: until then the order
    /// stays and is looked at again every frame. A Hologram comes only after the projector's await.
    /// </summary>
    public sealed class BeamToShipsSystem : IExecuteSystem
    {
        readonly PortContext _port;
        readonly IGroup<PortEntity> _orders;

        public BeamToShipsSystem(PortContext port)
        {
            _port = port;
            _orders = port.GetGroup(PortMatcher.BeamOrder);
        }

        public void Execute()
        {
            foreach (PortEntity order in _orders.GetEntities())
            {
                PortEntity ship = _port.GetEntityWithShipId(order.beamOrder.Value);
                if (ship != null && ship.hasHologram)
                {
                    ship.AddRepaintOrder(3);
                    order.Destroy();
                }
            }
        }
    }

    /// <summary>Seals every check, then destroys the sealed ones: its condition is a seal it puts on itself, so no check waits.</summary>
    public sealed class ClearCustomsSystem : IExecuteSystem
    {
        readonly IGroup<PortEntity> _checks;

        public ClearCustomsSystem(PortContext port) => _checks = port.GetGroup(PortMatcher.CustomsCheck);

        public void Execute()
        {
            foreach (PortEntity check in _checks.GetEntities())
                check.ReplaceSeal(1);
            foreach (PortEntity check in _checks.GetEntities())
                if (check.hasSeal)
                    check.Destroy();
        }
    }
}
