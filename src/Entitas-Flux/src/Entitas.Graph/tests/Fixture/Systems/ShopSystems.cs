using Entitas;

namespace Sample
{
    public sealed class BuyItemSystem : IExecuteSystem
    {
        public void Execute()
        {
            GameEntity item = CreateEntity.Empty();
            item.isShopItem = true;
            item.isUnpaid = true;
        }
    }

    /// <summary>Offers run out while unpaid: TimeIsUp, guarded by NoneOf like a done marker.</summary>
    public sealed class TickOffersSystem : IExecuteSystem
    {
        readonly IGroup<GameEntity> _offers;
        public int Ticks;

        public TickOffersSystem(GameContext game)
        {
            _offers = game.GetGroup(GameMatcher
                .AllOf(GameMatcher.ShopItem, GameMatcher.Unpaid)
                .NoneOf(GameMatcher.TimeIsUp));
        }

        public void Execute()
        {
            foreach (GameEntity offer in _offers.GetEntities())
            {
                if (++Ticks > 100)
                    offer.isTimeIsUp = true;
            }
        }
    }

    /// <summary>Marks expired offers Destructed in a helper: the TimeIsUp entities are destroyed, flag and all.</summary>
    public sealed class ExpireOffersSystem : IExecuteSystem
    {
        readonly IGroup<GameEntity> _expired;

        public ExpireOffersSystem(GameContext game) => _expired = game.GetGroup(GameMatcher.AllOf(GameMatcher.ShopItem, GameMatcher.TimeIsUp));

        public void Execute()
        {
            foreach (GameEntity offer in _expired.GetEntities())
                Expire(offer);
        }

        void Expire(GameEntity offer) => offer.isDestructed = true;
    }

    /// <summary>Removes Unpaid; the reaction to that is somebody else's.</summary>
    public sealed class CommitCartSystem : IExecuteSystem
    {
        readonly IGroup<GameEntity> _unpaid;

        public CommitCartSystem(GameContext game) => _unpaid = game.GetGroup(GameMatcher.AllOf(GameMatcher.ShopItem, GameMatcher.Unpaid));

        public void Execute()
        {
            foreach (GameEntity item in _unpaid.GetEntities())
                item.isUnpaid = false;
        }
    }

    public sealed class SendPurchaseEventOnPaidSystem : IExecuteSystem
    {
        readonly IGroup<GameEntity> _paid;

        public SendPurchaseEventOnPaidSystem(GameContext game)
        {
            _paid = game.GetGroup(GameMatcher
                .AllOf(GameMatcher.ShopItem, GameMatcher.UnpaidChanged)
                .NoneOf(GameMatcher.Unpaid, GameMatcher.Receipted));
        }

        public void Execute()
        {
            foreach (GameEntity item in _paid.GetEntities())
            {
                CreateEntity.Event().AddItemPurchasedEvent(1);
                item.isReceipted = true;
            }
        }
    }

    public sealed class CountPurchasesSystem : IExecuteSystem
    {
        readonly IGroup<GameEntity> _purchases;
        public int Count;

        public CountPurchasesSystem(GameContext game) => _purchases = game.GetGroup(GameMatcher.AllOf(GameMatcher.ItemPurchasedEvent, GameMatcher.Ready));

        public void Execute() => Count += _purchases.count;
    }

    public sealed class UnboxItemsSystem : IExecuteSystem
    {
        readonly IGroup<GameEntity> _boxed;

        public UnboxItemsSystem(GameContext game)
        {
            _boxed = game.GetGroup(GameMatcher
                .AllOf(GameMatcher.ShopItem)
                .NoneOf(GameMatcher.Unboxed));
        }

        public void Execute()
        {
            foreach (GameEntity item in _boxed.GetEntities())
                item.isUnboxed = true;
        }
    }

    /// <summary>Stores paid items: ProcessingStoring and Destructed on the same entity, in the same loop.</summary>
    public sealed class StoreItemsSystem : IExecuteSystem
    {
        readonly IGroup<GameEntity> _paid;

        public StoreItemsSystem(GameContext game)
        {
            _paid = game.GetGroup(GameMatcher
                .AllOf(GameMatcher.ShopItem)
                .NoneOf(GameMatcher.Unpaid));
        }

        public void Execute()
        {
            foreach (GameEntity item in _paid.GetEntities())
            {
                if (!item.isUnboxed || !item.isReceipted)
                    continue;

                item.isProcessingStoring = true;
                item.isDestructed = true;
            }
        }
    }

    public sealed class AnimateStoringSystem : IExecuteSystem
    {
        readonly IGroup<GameEntity> _storing;
        public int Animated;

        public AnimateStoringSystem(GameContext game) => _storing = game.GetGroup(GameMatcher.AllOf(GameMatcher.ShopItem, GameMatcher.ProcessingStoring));

        public void Execute() => Animated += _storing.count;
    }

    /// <summary>Pays the rent into a till, and has a shopkeeper built while there is none.</summary>
    public sealed class OpenShopSystem : IExecuteSystem
    {
        readonly ShopkeeperFactory _factory;
        readonly IGroup<GameEntity> _keepers;

        public OpenShopSystem(GameContext game, ShopkeeperFactory factory)
        {
            _factory = factory;
            _keepers = game.GetGroup(GameMatcher.Shopkeeper);
        }

        public void Execute()
        {
            CreateEntity.Empty().AddTill(-2);
            if (_keepers.count == 0)
                _factory.Create();
        }
    }

    /// <summary>Settles every till: one that went negative spent coins, any other earned them.</summary>
    public sealed class SettleTillSystem : IExecuteSystem
    {
        readonly IGroup<GameEntity> _tills;

        public SettleTillSystem(GameContext game) => _tills = game.GetGroup(GameMatcher.Till);

        public void Execute()
        {
            foreach (GameEntity till in _tills.GetEntities())
            {
                if (till.Till < 0)
                    CreateEntity.Event().AddCoinsSpentEvent(-till.Till);
                else
                    CreateEntity.Event().AddCoinsEarnedEvent(till.Till);
                till.RemoveTill();
            }
        }
    }

    public sealed class GreetCustomersSystem : IExecuteSystem
    {
        readonly IGroup<GameEntity> _opened;
        public int Greeted;

        public GreetCustomersSystem(GameContext game) => _opened = game.GetGroup(GameMatcher.AllOf(GameMatcher.ShopOpenedEvent, GameMatcher.Ready));

        public void Execute() => Greeted += _opened.count;
    }

    public sealed class CountCoinsSystem : IExecuteSystem
    {
        readonly IGroup<GameEntity> _spent;
        readonly IGroup<GameEntity> _earned;
        public int Balance;

        public CountCoinsSystem(GameContext game)
        {
            _spent = game.GetGroup(GameMatcher.AllOf(GameMatcher.CoinsSpentEvent, GameMatcher.Ready));
            _earned = game.GetGroup(GameMatcher.AllOf(GameMatcher.CoinsEarnedEvent, GameMatcher.Ready));
        }

        public void Execute() => Balance += _earned.count - _spent.count;
    }
}
