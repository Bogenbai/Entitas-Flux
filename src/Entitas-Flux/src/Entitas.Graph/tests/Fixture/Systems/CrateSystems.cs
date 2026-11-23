using System.Collections.Generic;
using Entitas;

namespace Sample
{
    public sealed class SpawnCrateSystem : IExecuteSystem
    {
        public void Execute()
        {
            GameEntity crate = CreateEntity.Empty();
            crate.isCrate = true;
            crate.isJustSpawned = true;
            crate.isWrapped = true;
            crate.isFragile = true;
            crate.isStacked = true;
        }
    }

    /// <summary>Sees each crate once: its group requires JustSpawned, which no crate keeps.</summary>
    public sealed class RestoreCrateSystem : IExecuteSystem
    {
        readonly IGroup<GameEntity> _spawned;

        public RestoreCrateSystem(GameContext game) => _spawned = game.GetGroup(GameMatcher.AllOf(GameMatcher.Crate, GameMatcher.JustSpawned));

        public void Execute()
        {
            foreach (GameEntity crate in _spawned)
                crate.isCrateRestored = true;
        }
    }

    /// <summary>Sees every crate, every frame.</summary>
    public sealed class LabelCratesSystem : IExecuteSystem
    {
        readonly IGroup<GameEntity> _crates;

        public LabelCratesSystem(GameContext game) => _crates = game.GetGroup(GameMatcher.AllOf(GameMatcher.Crate));

        public void Execute()
        {
            foreach (GameEntity crate in _crates.GetEntities())
                crate.isCrateLabelled = true;
        }
    }

    public sealed class LoadCratesSystem : IExecuteSystem
    {
        readonly IGroup<GameEntity> _loadable;
        public int Loaded;

        public LoadCratesSystem(GameContext game) => _loadable = game.GetGroup(GameMatcher.AllOf(GameMatcher.CrateRestored, GameMatcher.CrateLabelled));

        public void Execute() => Loaded += _loadable.count;
    }

    /// <summary>Takes Fragile off labelled crates only — after a continue, not under an if.</summary>
    public sealed class PackFragileCratesSystem : IExecuteSystem
    {
        readonly IGroup<GameEntity> _fragile;

        public PackFragileCratesSystem(GameContext game) => _fragile = game.GetGroup(GameMatcher.Fragile);

        public void Execute()
        {
            foreach (GameEntity crate in _fragile.GetEntities())
            {
                if (!crate.isCrateLabelled)
                    continue;

                crate.isFragile = false;
            }
        }
    }

    /// <summary>Takes Stacked off every crate it sees, but its NoneOf spares the wrapped ones.</summary>
    public sealed class UnstackCratesSystem : IExecuteSystem
    {
        readonly IGroup<GameEntity> _stacked;

        public UnstackCratesSystem(GameContext game)
        {
            _stacked = game.GetGroup(GameMatcher
                .AllOf(GameMatcher.Stacked)
                .NoneOf(GameMatcher.Wrapped));
        }

        public void Execute()
        {
            foreach (GameEntity crate in _stacked.GetEntities())
                crate.isStacked = false;
        }
    }

    public sealed class UnwrapCratesSystem : IExecuteSystem
    {
        readonly IGroup<GameEntity> _wrapped;

        public UnwrapCratesSystem(GameContext game) => _wrapped = game.GetGroup(GameMatcher.Wrapped);

        public void Execute()
        {
            foreach (GameEntity crate in _wrapped.GetEntities())
                crate.RemoveComponent(GameComponentsLookup.Wrapped);
        }
    }

    /// <summary>
    /// Takes JustSpawned off every crate holding it. The NoneOf skips only the crates this loop
    /// already handled (it marks them SpawnSeen), so it does not leave any crate with the marker.
    /// </summary>
    public sealed class UnmarkJustSpawnedSystem : ICleanupSystem
    {
        readonly IGroup<GameEntity> _spawned;
        readonly List<GameEntity> _buffer = new List<GameEntity>();

        public UnmarkJustSpawnedSystem(GameContext game)
        {
            _spawned = game.GetGroup(GameMatcher
                .AllOf(GameMatcher.JustSpawned)
                .NoneOf(GameMatcher.SpawnSeen));
        }

        public void Cleanup()
        {
            foreach (GameEntity crate in _spawned.GetEntities(_buffer))
            {
                crate.isJustSpawned = false;
                crate.isSpawnSeen = true;
            }
        }
    }
}
