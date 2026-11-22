using Entitas;

namespace Sample
{
    public interface ISystemFactory
    {
        T Create<T>() where T : ISystem;
    }

    public static class CreateEntity
    {
        static Contexts _contexts;

        public static void SetContexts(Contexts contexts) => _contexts = contexts;

        public static GameEntity Empty() => _contexts.game.CreateEntity();

        public static GameEntity Event()
        {
            GameEntity entity = _contexts.game.CreateEntity();
            entity.isEvent = true;
            return entity;
        }
    }

    /// <summary>A factory: systems calling it create what it adds.</summary>
    public sealed class FurnitureFactory
    {
        public GameEntity CreateFurniture(int id)
        {
            GameEntity furniture = CreateEntity.Empty();
            furniture.isFurniture = true;
            furniture.AddHealth(10);
            furniture.AddId(id);
            return furniture;
        }
    }

    public interface IFurnitureQuery
    {
        int Count();
    }

    /// <summary>A query: its group is read by whoever asks it, through the interface.</summary>
    public sealed class FurnitureQuery : IFurnitureQuery
    {
        readonly IGroup<GameEntity> _furniture;

        public FurnitureQuery(GameContext game)
        {
            _furniture = game.GetGroup(GameMatcher
                .AllOf(GameMatcher.Furniture)
                .NoneOf(GameMatcher.Destructed));
        }

        public int Count() => _furniture.count;
    }
}
