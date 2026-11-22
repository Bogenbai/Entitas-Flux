namespace Sample
{
    public sealed class GameplayFeature : Feature
    {
        public GameplayFeature(ISystemFactory systems, Contexts contexts)
        {
            Add(systems.Create<MarkEventsReadySystem>());
            Add(systems.Create<FurnitureFeature>());
            Add(systems.Create<UnlockAchievementSystem>());
            Add(new GameCleanupSystems(contexts));
        }
    }

    public sealed class FurnitureFeature : Feature
    {
        public FurnitureFeature(ISystemFactory systems)
        {
            Add(systems.Create<RequestPlacementSystem>());
            Add(systems.Create<PlaceFurnitureByRequestSystem>());
            Add(systems.Create<CountFurnitureSystem>());
            Add(systems.Create<SelectSystem>());
            Add(systems.Create<HighlightOnSelectedSystem>());
            Add(systems.Create<DeselectSystem>());
            Add(systems.Create<DamageSystem>());
            Add(systems.Create<UnusedSystem>());
        }
    }

    public sealed class LateFeature : Feature
    {
        public LateFeature(ISystemFactory systems)
        {
            Add(systems.Create<DestroyEventsSystem>());
            Add(systems.Create<DestroyDestructedSystem>());
        }
    }
}
