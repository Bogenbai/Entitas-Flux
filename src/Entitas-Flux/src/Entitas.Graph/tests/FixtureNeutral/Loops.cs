namespace Harbor
{
    /// <summary>The frame: pulses are armed first, so a pulse made this frame is seen armed the next.</summary>
    public sealed class CoreLoop : Feature
    {
        public CoreLoop(Contexts contexts, ShipFactory factory)
        {
            Add(new ArmPulsesSystem(contexts.port));
            Add(new TickClockSystem(contexts.signal));
            Add(new Berthing(contexts));
            Add(new GreetCaptainSystem(contexts.port));
            Add(new UnloadCargoSystem(contexts.port));
            Add(new CountCargoSystem(contexts.port));
            Add(new CountShipsDockedSystem(contexts.port));
            Add(new Fleet(contexts, factory));
            Add(new ForgetFreshSystem(contexts.port));
            Add(new PortWatchedCleanupSystems(contexts));
        }
    }

    public sealed class Berthing : Feature
    {
        public Berthing(Contexts contexts)
        {
            Add(new IssueDockingOrdersSystem(contexts.signal));
            Add(new LogOrdersSystem(contexts.port));
            Add(new DockShipsSystem(contexts.port));
            Add(new RefuelShipsSystem(contexts.port));
            Add(new SellShipsSystem(contexts.port));
        }
    }

    public sealed class Fleet : Feature
    {
        public Fleet(Contexts contexts, ShipFactory factory)
        {
            Add(new LaunchShipsSystem(contexts.signal, factory));
            Add(new RegisterShipsSystem(contexts.port));
            Add(new BurnFuelSystem(contexts.port));
            Add(new WarnLowFuelSystem(contexts.port));
            Add(new PrioritizeShipsSystem(contexts.port));
            Add(new ScrapShipsSystem(contexts.port));
            Add(new ScrapFleetSystem(contexts.port));
            Add(new LaunchTankersSystem(contexts.signal, factory));
        }
    }

    /// <summary>A root of its own, run after the frame: the sweepers.</summary>
    public sealed class LateLoop : Feature
    {
        public LateLoop(Contexts contexts)
        {
            Add(new SweepPulsesSystem(contexts.port));
            Add(new SweepDoomedSystem(contexts.port));
        }
    }
}
