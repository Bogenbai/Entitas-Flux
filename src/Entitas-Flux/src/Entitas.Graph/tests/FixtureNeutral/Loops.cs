namespace Harbor
{
    /// <summary>The frame: pulses are armed first, so a pulse made this frame is seen armed the next.</summary>
    public sealed class CoreLoop : Feature
    {
        public CoreLoop(Contexts contexts, ShipFactory factory, DepotFactory depots)
        {
            Add(new ArmPulsesSystem(contexts.port));
            Add(new TickClockSystem(contexts.signal));
            Add(new Berthing(contexts));
            Add(new GreetCaptainSystem(contexts.port));
            Add(new UnloadCargoSystem(contexts.port));
            Add(new CountCargoSystem(contexts.port));
            Add(new CountShipsDockedSystem(contexts.port));
            Add(new Fleet(contexts, factory));
            Add(new Depots(contexts, depots));
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

    public sealed class Depots : Feature
    {
        public Depots(Contexts contexts, DepotFactory factory)
        {
            Add(new ShowGaugesSystem(contexts.port));
            Add(new FitGaugesSystem(contexts.port));
            Add(new BuildDepotsSystem(contexts.signal, factory));
            Add(new LeakFuelSystem(contexts.port));
        }
    }

    /// <summary>A root of its own, run after the frame: the sweepers, the yard, then the probes.</summary>
    public sealed class LateLoop : Feature
    {
        public LateLoop(Contexts contexts)
        {
            Add(new SweepPulsesSystem(contexts.port));
            Add(new SweepDoomedSystem(contexts.port));
            Add(new Yard(contexts));
            Add(new Probes(contexts));
        }
    }

    /// <summary>Probes are charged, deployed, projected, then launched and pinged.</summary>
    public sealed class Probes : Feature
    {
        public Probes(Contexts contexts)
        {
            Add(new ChargeProbesSystem(contexts.port));
            Add(new DeployProbesSystem());
            Add(new RedeployProbesSystem());
            Add(new ProjectHologramsSystem(contexts.port));
            Add(new LaunchProbesSystem(contexts.port));
            Add(new PingProbesSystem(contexts.port));
        }
    }

    /// <summary>Work on ships that are already there, asked for or not.</summary>
    public sealed class Yard : Feature
    {
        public Yard(Contexts contexts)
        {
            Add(new RepaintHullsSystem(contexts.port));
            Add(new TowHullsSystem(contexts.signal, contexts.port));
            Add(new AnswerHailsSystem(contexts.port));
            Add(new LogSightingsSystem(contexts.port));
            Add(new HoistColoursSystem(contexts.port));
            Add(new StrikeColoursSystem(contexts.port));
            Add(new SurveyHullsSystem(contexts.port));
            Add(new ReturnSalutesSystem(contexts.port));
            Add(new PilotShipsSystem(contexts.port));
            Add(new LightBeaconsSystem(contexts.port));
            Add(new WatchBeaconsSystem(contexts.port));
            Add(new DouseBeaconsSystem(contexts.port));
        }
    }
}
