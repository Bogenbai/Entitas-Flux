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

    public sealed class LateFeature : Feature
    {
        public LateFeature(ISystemFactory systems)
        {
            Add(systems.Create<DestroyEventsSystem>());
            Add(systems.Create<DestroyDestructedSystem>());
        }
    }
}
