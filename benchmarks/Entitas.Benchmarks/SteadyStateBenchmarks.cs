using BenchmarkDotNet.Attributes;
using Entitas;

namespace Entitas.Benchmarks
{
    /// <summary>
    /// What a running game actually does: one long-lived context in which entities are
    /// created and destroyed over and over.
    ///
    /// CoreBenchmarks.CreateDestroy builds a FRESH context every iteration and keeps all
    /// 10k entities alive at once, which defeats both of the framework's pools —
    /// destroyed entities are pushed onto a reusable stack and components go back into
    /// per-index pools. It therefore measures cold allocation, not the steady state, and
    /// its ~5 MB per iteration says nothing about frame-time GC pressure.
    /// </summary>
    [Config(typeof(FastConfig))]
    public class SteadyStateBenchmarks
    {
        const int Batch = 1_000;
        const int Warmups = 5;

        Context<Entity> _ctx;

        [GlobalSetup]
        public void Setup()
        {
            _ctx = BenchEntityExtensions.NewContext();
            _ctx.GetGroup(Matcher<Entity>.AllOf(CompId.Position, CompId.Velocity));

            // Fill the entity and component pools, the way a game does within its first
            // seconds; from here on a create/destroy cycle should allocate nothing.
            for (var warmup = 0; warmup < Warmups; warmup++)
                CreateAndDestroyBatch();
        }

        [Benchmark]
        public void CreateDestroyBatch() => CreateAndDestroyBatch();

        void CreateAndDestroyBatch()
        {
            for (var i = 0; i < Batch; i++)
            {
                var e = _ctx.CreateEntity();
                e.AddPosition(i, i, i);
                e.AddVelocity(1, 1, 1);
            }

            _ctx.DestroyAllEntities();
        }
    }
}
