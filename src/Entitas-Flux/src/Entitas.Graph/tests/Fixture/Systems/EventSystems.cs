using System.Collections.Generic;
using Entitas;

namespace Sample
{
    public sealed class UnlockAchievementSystem : IExecuteSystem
    {
        readonly IGroup<GameEntity> _events;
        public bool Unlocked;

        public UnlockAchievementSystem(GameContext game)
        {
            _events = game.GetGroup(GameMatcher
                .AllOf(
                    GameMatcher.FurniturePlacedEvent,
                    GameMatcher.Ready));
        }

        public void Execute() => Unlocked |= _events.count > 0;
    }

    public sealed class MarkEventsReadySystem : IExecuteSystem
    {
        readonly IGroup<GameEntity> _events;
        readonly List<GameEntity> _buffer = new List<GameEntity>();

        public MarkEventsReadySystem(GameContext game)
        {
            _events = game.GetGroup(GameMatcher
                .AllOf(GameMatcher.Event)
                .NoneOf(GameMatcher.Ready));
        }

        public void Execute()
        {
            foreach (GameEntity entity in _events.GetEntities(_buffer))
                entity.isReady = true;
        }
    }

    public sealed class DestroyEventsSystem : IExecuteSystem
    {
        readonly IGroup<GameEntity> _events;
        readonly List<GameEntity> _buffer = new List<GameEntity>();

        public DestroyEventsSystem(GameContext game)
        {
            _events = game.GetGroup(GameMatcher.AllOf(GameMatcher.Event, GameMatcher.Ready));
        }

        public void Execute()
        {
            foreach (GameEntity entity in _events.GetEntities(_buffer))
                entity.Destroy();
        }
    }

    public sealed class DestroyDestructedSystem : IExecuteSystem
    {
        readonly IGroup<GameEntity> _destructed;
        readonly List<GameEntity> _buffer = new List<GameEntity>();

        public DestroyDestructedSystem(GameContext game) => _destructed = game.GetGroup(GameMatcher.Destructed);

        public void Execute()
        {
            foreach (GameEntity entity in _destructed.GetEntities(_buffer))
                entity.Destroy();
        }
    }
}
