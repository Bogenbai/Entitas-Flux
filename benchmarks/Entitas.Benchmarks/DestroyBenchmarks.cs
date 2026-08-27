using BenchmarkDotNet.Attributes;
using Entitas;

namespace Entitas.Benchmarks
{
    // Replicates the Frozen Feast collision-churn pattern: a context with many
    // component types (622) but entities that only use a handful of (high-index)
    // components, created and destroyed every frame.
    [Config(typeof(FastConfig))]
    public class DestroyBenchmarks
    {
        const int Total = 622;
        const int N = 2000;
        static readonly int[] Indices = { 600, 605, 610, 615 }; // collision-like, sparse + high

        Context<Entity> _ctx;
        Entity[] _live;

        [GlobalSetup]
        public void Setup()
        {
            _ctx = new Context<Entity>(Total, () => new Entity());
            _live = new Entity[N];

            // Warm the entity + component pools so steady-state churn is measured
            // (no first-time array allocations).
            for (var i = 0; i < N; i++)
            {
                var e = _ctx.CreateEntity();
                AddSparse(e);
                _live[i] = e;
            }

            for (var i = 0; i < N; i++)
                _live[i].Destroy();
        }

        static void AddSparse(Entity e)
        {
            for (var k = 0; k < Indices.Length; k++)
            {
                var index = Indices[k];
                var pool = e.GetComponentPool(index);
                var c = pool.Count > 0 ? (HiComponent)pool.Pop() : new HiComponent();
                e.AddComponent(index, c);
            }
        }

        [Benchmark]
        public void CreateDestroySparseChurn()
        {
            var live = _live;
            for (var i = 0; i < N; i++)
            {
                var e = _ctx.CreateEntity();
                AddSparse(e);
                live[i] = e;
            }

            for (var i = 0; i < N; i++)
                live[i].Destroy();
        }
    }

    public sealed class HiComponent : IComponent { public int Value; }
}
