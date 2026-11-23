using System;

namespace Harbor
{
    /// <summary>Where entities come from: a blank one, or a pulse — an entity the sweeper takes away once it is armed.</summary>
    public static class Make
    {
        static Contexts _contexts;

        public static void Use(Contexts contexts) => _contexts = contexts;

        public static PortEntity Blank() => _contexts.port.CreateEntity();

        public static PortEntity Pulse()
        {
            PortEntity pulse = _contexts.port.CreateEntity();
            pulse.isPulse = true;
            return pulse;
        }
    }

    /// <summary>
    /// Helpers that run what they are handed or not. Their parameters are named `gate` and
    /// `skip`: whether the lambda runs is in their bodies, not in their names.
    /// </summary>
    public static class Fluent
    {
        public static T Apply<T>(this T self, Action<T> apply, bool gate)
        {
            if (gate)
                apply(self);
            return self;
        }

        public static T Unless<T>(this T self, Action<T> apply, bool skip)
        {
            if (skip)
                return self;
            apply(self);
            return self;
        }

        public static T Also<T>(this T self, Action<T> also)
        {
            also(self);
            return self;
        }
    }

    /// <summary>A depot is built with what it is, then drained: Drain changes the [Watched] Fuel of the depot being built.</summary>
    public sealed class DepotFactory
    {
        public PortEntity Build(int capacity)
        {
            PortEntity depot = Make.Blank().Also(x => x.isDepot = true);
            depot.AddFuel(capacity);
            return depot.Drain();
        }
    }

    public static class Tanks
    {
        public static PortEntity Drain(this PortEntity tank)
        {
            tank.ReplaceFuel(0);
            return tank;
        }
    }

    /// <summary>A factory: a ship is built with everything it carries, a tanker with a tank too.</summary>
    public sealed class ShipFactory
    {
        public PortEntity Launch(int id, ShipKind kind)
        {
            PortEntity ship = Make.Blank();
            ship.isShip = true;
            ship.AddShipId(id);
            ship.AddFuel(50);
            ship.AddCargo(id * 40);
            ship.AddHull(3);
            switch (kind)
            {
                case ShipKind.Tanker:
                    ship.AddTank(id * 100);
                    break;
            }
            ship.isFresh = true;
            return ship;
        }
    }

    /// <summary>The harbour master's panel: orders come from the UI as well as from systems.</summary>
    public sealed class DockingPanel
    {
        public void OnDockPressed(int shipId) => Make.Blank().AddDockingOrder(shipId);

        public void OnSellPressed(int shipId) => Make.Blank().AddSaleOrder(shipId);

        public void OnScrapFleetPressed() => Make.Blank().isScrapFleetOrder = true;

        // The yard's buttons act on a ship that is already there, but for the tow.
        public void OnRepaintPressed(PortEntity ship, int colour) => ship.AddRepaintOrder(colour);

        public void OnTowPressed(int shipId) => Make.Blank().AddTowOrder(shipId);

        public void OnHailPressed(PortEntity ship) => ship.isHailed = true;

        public void OnSightPressed(PortEntity ship) => ship.isSighted = true;

        public void OnSalutePressed(PortEntity ship) => ship.isSalute = true;

        public void OnPilotPressed(int shipId) => Make.Blank().AddPilotOrder(shipId);

        public void OnBeamPressed(int shipId) => Make.Blank().AddBeamOrder(shipId);

        public void OnCustomsPressed(int shipId) => Make.Blank().AddCustomsCheck(shipId);
    }

    /// <summary>Paints whatever hull it is handed: the yard's work happens in here, not in the yard's loop.</summary>
    public static class Brushes
    {
        public static void Coat(PortEntity hull, int colour) => hull.ReplacePaint(colour);
    }

    /// <summary>Waits for a hologram's asset, then has the emitter mount it: the Hologram arrives after an await.</summary>
    public sealed class HoloProjector
    {
        readonly IFitting[] _fittings = { new LensFitting() };

        public async System.Threading.Tasks.Task ProjectAsync(PortEntity probe)
        {
            await System.Threading.Tasks.Task.Delay(10);
            new HoloEmitter().Mount(probe);
            foreach (IFitting fitting in _fittings)
                fitting.Fit(probe);
        }
    }

    // Fittings are reached through an interface an abstract class implements: the lens is fitted
    // by an override two steps from the call, still after the projector's await.
    public interface IFitting
    {
        void Fit(PortEntity probe);
    }

    public abstract class Fitting : IFitting
    {
        public abstract void Fit(PortEntity probe);
    }

    public sealed class LensFitting : Fitting
    {
        public override void Fit(PortEntity probe) => probe.ReplaceLens(1);
    }

    public sealed class HoloEmitter
    {
        public void Mount(PortEntity probe) => probe.AddHologram(this);
    }
}
