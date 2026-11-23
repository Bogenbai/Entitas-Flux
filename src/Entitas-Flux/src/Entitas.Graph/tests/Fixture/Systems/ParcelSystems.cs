using Entitas;

namespace Sample
{
    /// <summary>Stamps every parcel without a stamp: a plain system, ahead of the one making parcels.</summary>
    public sealed class StampParcelsSystem : IExecuteSystem
    {
        readonly IGroup<GameEntity> _unstamped;

        public StampParcelsSystem(GameContext game) => _unstamped = game.GetGroup(GameMatcher.AllOf(GameMatcher.Parcel).NoneOf(GameMatcher.Stamp));

        public void Execute()
        {
            foreach (GameEntity parcel in _unstamped.GetEntities())
                parcel.AddStamp(1);
        }
    }

    /// <summary>Makes a parcel with its model's asset — no Model, no Stamp yet — and asks for it to be dispatched and franked.</summary>
    public sealed class ReturnParcelsSystem : IExecuteSystem
    {
        public void Execute()
        {
            GameEntity parcel = CreateEntity.Empty();
            parcel.isParcel = true;
            parcel.AddModelAsset("parcel");
            parcel.isDispatchPending = true;
            parcel.isFrankPending = true;
        }
    }

    /// <summary>Makes a parcel with its Model already attached: its handler sees it by order alone.</summary>
    public sealed class ResendParcelsSystem : IExecuteSystem
    {
        public void Execute()
        {
            GameEntity parcel = CreateEntity.Empty();
            parcel.isParcel = true;
            parcel.AddModel(new object());
            parcel.isDispatchPending = true;
        }
    }

    /// <summary>Starts loading the model of every parcel without one; the loader attaches it when the asset is there.</summary>
    public sealed class LoadModelsSystem : IExecuteSystem
    {
        readonly ModelLoader _loader;
        readonly IGroup<GameEntity> _unloaded;

        public LoadModelsSystem(GameContext game, ModelLoader loader)
        {
            _loader = loader;
            _unloaded = game.GetGroup(GameMatcher.AllOf(GameMatcher.ModelAsset).NoneOf(GameMatcher.Model));
        }

        public void Execute()
        {
            foreach (GameEntity parcel in _unloaded.GetEntities())
                _ = _loader.LoadAsync(parcel);
        }
    }

    public sealed class DispatchParcelsSystem : IExecuteSystem
    {
        readonly IGroup<GameEntity> _pending;

        public DispatchParcelsSystem(GameContext game) =>
            _pending = game.GetGroup(GameMatcher.AllOf(GameMatcher.Parcel, GameMatcher.DispatchPending, GameMatcher.Model));

        public void Execute()
        {
            foreach (GameEntity parcel in _pending.GetEntities())
            {
                parcel.ReplacePostage(1);
                parcel.isDispatchPending = false;
            }
        }
    }

    /// <summary>Reads its request on the entity its loop visits, too: still only through its group.</summary>
    public sealed class FrankParcelsSystem : IExecuteSystem
    {
        readonly IGroup<GameEntity> _pending;

        public FrankParcelsSystem(GameContext game) =>
            _pending = game.GetGroup(GameMatcher.AllOf(GameMatcher.Parcel, GameMatcher.FrankPending, GameMatcher.Stamp));

        public void Execute()
        {
            foreach (GameEntity parcel in _pending.GetEntities())
            {
                parcel.ReplacePostage(parcel.isFrankPending ? parcel.Stamp : 0);
                parcel.isFrankPending = false;
            }
        }
    }
}
