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
            Add(systems.Create<ShopFeature>());
        }
    }

    public sealed class ShopFeature : Feature
    {
        public ShopFeature(ISystemFactory systems)
        {
            Add(systems.Create<BuyItemSystem>());
            Add(systems.Create<TickOffersSystem>());
            Add(systems.Create<ExpireOffersSystem>());
            Add(systems.Create<CommitCartSystem>());
            Add(systems.Create<SendPurchaseEventOnPaidSystem>());
            Add(systems.Create<CountPurchasesSystem>());
            Add(systems.Create<UnboxItemsSystem>());
            Add(systems.Create<StoreItemsSystem>());
            Add(systems.Create<AnimateStoringSystem>());
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

    /// <summary>A root of its own, so the crates move no order or query of the features above.</summary>
    public sealed class CrateFeature : Feature
    {
        public CrateFeature(ISystemFactory systems)
        {
            Add(systems.Create<SpawnCrateSystem>());
            Add(systems.Create<RestoreCrateSystem>());
            Add(systems.Create<LabelCratesSystem>());
            Add(systems.Create<LoadCratesSystem>());
            Add(systems.Create<PackFragileCratesSystem>());
            Add(systems.Create<UnstackCratesSystem>());
            Add(systems.Create<UnwrapCratesSystem>());
            Add(systems.Create<UnmarkJustSpawnedSystem>());
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
