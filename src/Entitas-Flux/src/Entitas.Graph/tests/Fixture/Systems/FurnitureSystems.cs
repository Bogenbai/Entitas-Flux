using System.Collections.Generic;
using Entitas;

namespace Sample
{
    public sealed class RequestPlacementSystem : IExecuteSystem
    {
        readonly InputContext _input;

        public RequestPlacementSystem(InputContext input) => _input = input;

        public void Execute()
        {
            if (!_input.hasPointer)
                return;

            CreateEntity.Empty().AddPlaceFurnitureRequest(_input.pointerEntity.Pointer);
            CreateEntity.Empty().isOrphanRequest = true;
        }
    }

    public sealed class PlaceFurnitureByRequestSystem : IExecuteSystem
    {
        readonly FurnitureFactory _factory;
        readonly IGroup<GameEntity> _requests;
        readonly List<GameEntity> _buffer = new List<GameEntity>();

        public PlaceFurnitureByRequestSystem(GameContext game, FurnitureFactory factory)
        {
            _factory = factory;
            _requests = game.GetGroup(GameMatcher.PlaceFurnitureRequest);
        }

        public void Execute()
        {
            foreach (GameEntity request in _requests.GetEntities(_buffer))
            {
                _factory.CreateFurniture(request.PlaceFurnitureRequest);
                CreateEntity.Event().AddFurniturePlacedEvent(request.PlaceFurnitureRequest);
                request.isDestructed = true;
            }
        }
    }

    public sealed class CountFurnitureSystem : IExecuteSystem
    {
        readonly IFurnitureQuery _query;
        public int Last;

        public CountFurnitureSystem(IFurnitureQuery query) => _query = query;

        public void Execute() => Last = _query.Count();
    }

    public sealed class SelectSystem : IExecuteSystem
    {
        readonly IGroup<GameEntity> _candidates;

        public SelectSystem(GameContext game)
        {
            _candidates = game.GetGroup(GameMatcher
                .AllOf(GameMatcher.Furniture)
                .NoneOf(GameMatcher.Selected));
        }

        public void Execute()
        {
            GameEntity[] candidates = _candidates.GetEntities();
            foreach (GameEntity candidate in candidates)
            {
                candidate.isSelected = true;
                candidate.isTapped = true;
                candidate.isInspected = true;
            }
        }
    }

    public sealed class HighlightOnSelectedSystem : IExecuteSystem
    {
        readonly IGroup<GameEntity> _changed;

        public HighlightOnSelectedSystem(GameContext game)
        {
            _changed = game.GetGroup(GameMatcher.AllOf(GameMatcher.Furniture, GameMatcher.SelectedChanged));
        }

        public void Execute()
        {
            foreach (GameEntity entity in _changed)
            {
                if (entity.isInspected) { }
            }
        }
    }

    public sealed class DeselectSystem : IExecuteSystem
    {
        readonly IGroup<GameEntity> _selected;

        public DeselectSystem(GameContext game) => _selected = game.GetGroup(GameMatcher.Selected);

        public void Execute()
        {
            foreach (GameEntity entity in _selected.GetEntities())
                entity.isSelected = false;
        }
    }

    public sealed class DamageSystem : IExecuteSystem
    {
        readonly GameContext _game;
        readonly IGroup<GameEntity> _alive;

        public DamageSystem(GameContext game)
        {
            _game = game;
            _alive = game.GetGroup(GameMatcher.AllOf(GameMatcher.Health, GameMatcher.Id));
        }

        public void Execute()
        {
            foreach (GameEntity entity in _alive.GetEntities())
            {
                entity.ReplaceHealth(entity.Health - 1);
                if (entity.isDead)
                    entity.RemoveHealth();
            }

            _game.GetEntityWithId(1)?.SafeRemoveHealth();
        }
    }

    public sealed class UnusedSystem : IExecuteSystem
    {
        public void Execute() => CreateEntity.Empty().isUnused = true;
    }

    /// <summary>Registered nowhere.</summary>
    public sealed class OrphanSystem : IExecuteSystem
    {
        public void Execute() { }
    }
}
