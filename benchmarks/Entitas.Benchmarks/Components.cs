using System;
using Entitas;

namespace Entitas.Benchmarks
{
    // Minimal hand-written components and an entity factory that mirror what the
    // code generator produces, so the benchmarks exercise the real core runtime
    // (Context / Entity / Group / Matcher / EntityIndex) without depending on
    // generated user code.

    public sealed class PositionComponent : IComponent { public float X, Y, Z; }
    public sealed class VelocityComponent : IComponent { public float X, Y, Z; }
    public sealed class HealthComponent : IComponent { public int Value; }
    public sealed class NameComponent : IComponent { public string Value; }
    public sealed class TagAComponent : IComponent { }
    public sealed class TagBComponent : IComponent { }

    public static class CompId
    {
        public const int Position = 0;
        public const int Velocity = 1;
        public const int Health = 2;
        public const int Name = 3;
        public const int TagA = 4;
        public const int TagB = 5;
        public const int Total = 6;
    }

    // Extension helpers mirroring the generated Add/Replace/Remove API surface,
    // using the pooled CreateComponent path exactly like generated code.
    public static class BenchEntityExtensions
    {
        public static Context<Entity> NewContext() =>
            new Context<Entity>(CompId.Total, () => new Entity());

        public static void AddPosition(this Entity e, float x, float y, float z)
        {
            var c = (PositionComponent)e.CreateComponent(CompId.Position, typeof(PositionComponent));
            c.X = x; c.Y = y; c.Z = z;
            e.AddComponent(CompId.Position, c);
        }

        public static void ReplacePosition(this Entity e, float x, float y, float z)
        {
            var c = (PositionComponent)e.CreateComponent(CompId.Position, typeof(PositionComponent));
            c.X = x; c.Y = y; c.Z = z;
            e.ReplaceComponent(CompId.Position, c);
        }

        public static void AddVelocity(this Entity e, float x, float y, float z)
        {
            var c = (VelocityComponent)e.CreateComponent(CompId.Velocity, typeof(VelocityComponent));
            c.X = x; c.Y = y; c.Z = z;
            e.AddComponent(CompId.Velocity, c);
        }

        public static void AddHealth(this Entity e, int value)
        {
            var c = (HealthComponent)e.CreateComponent(CompId.Health, typeof(HealthComponent));
            c.Value = value;
            e.AddComponent(CompId.Health, c);
        }

        public static void ReplaceHealth(this Entity e, int value)
        {
            var c = (HealthComponent)e.CreateComponent(CompId.Health, typeof(HealthComponent));
            c.Value = value;
            e.ReplaceComponent(CompId.Health, c);
        }

        public static void AddTagA(this Entity e)
        {
            var c = (TagAComponent)e.CreateComponent(CompId.TagA, typeof(TagAComponent));
            e.AddComponent(CompId.TagA, c);
        }

        public static void RemoveTagA(this Entity e) => e.RemoveComponent(CompId.TagA);

        public static int HealthKey(Entity e, IComponent c) =>
            ((HealthComponent)(c ?? e.GetComponent(CompId.Health))).Value;

        // --- "Optimized generated code" variant: inline pool + concrete new,
        //     mirroring the new generator templates (no Activator.CreateInstance).

        public static void AddPositionNew(this Entity e, float x, float y, float z)
        {
            const int index = CompId.Position;
            var pool = e.GetComponentPool(index);
            var c = pool.Count > 0 ? (PositionComponent)pool.Pop() : new PositionComponent();
            c.X = x; c.Y = y; c.Z = z;
            e.AddComponent(index, c);
        }

        public static void AddVelocityNew(this Entity e, float x, float y, float z)
        {
            const int index = CompId.Velocity;
            var pool = e.GetComponentPool(index);
            var c = pool.Count > 0 ? (VelocityComponent)pool.Pop() : new VelocityComponent();
            c.X = x; c.Y = y; c.Z = z;
            e.AddComponent(index, c);
        }
    }
}
