using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Configs;
// The namespace is needed for Morpeh's extension methods (CreateEntity, GetStash,
// Commit); the alias keeps its Entity/World distinct from Entitas's, since both
// frameworks call their central types the same thing.
using Scellecs.Morpeh;
using Morpeh = Scellecs.Morpeh;

namespace Entitas.Benchmarks
{
    // Morpeh-side components: structs, as that framework requires.
    public struct MorpehPosition : Morpeh.IComponent { public float X, Y, Z; }
    public struct MorpehVelocity : Morpeh.IComponent { public float X, Y, Z; }
    public struct MorpehTagA : Morpeh.IComponent { }

    /// <summary>
    /// Entitas-Flux against Morpeh on the same scenarios, in one process and one table.
    ///
    /// Read the numbers as orientation, not as a verdict: these are different designs,
    /// not two implementations of one design. Morpeh stores struct components in stashes
    /// and updates its filters when the world is committed; Entitas stores pooled class
    /// components and updates groups eagerly on every change. A scenario that commits
    /// once per batch flatters Morpeh; one that queries after every change flatters
    /// Entitas. Both are written here the way each framework is meant to be used.
    /// </summary>
    [Config(typeof(FastConfig))]
    [GroupBenchmarksBy(BenchmarkLogicalGroupRule.ByCategory)]
    [CategoriesColumn]
    public class MorpehComparisonBenchmarks
    {
        const int N = 10_000;

        Context<Entitas.Entity> _ctx;
        Entitas.Entity[] _entities;
        IGroup<Entitas.Entity> _group;

        Morpeh.World _world;
        Morpeh.Entity[] _morpehEntities;
        Morpeh.Filter _filter;
        Morpeh.Stash<MorpehPosition> _positions;
        Morpeh.Stash<MorpehVelocity> _velocities;
        Morpeh.Stash<MorpehTagA> _tags;
        Morpeh.Entity[] _batch;
        Entitas.Entity[] _batchFlux;

        // ---- create / destroy inside a live world -------------------------
        // NOT "build a world, fill it, throw it away": Morpeh's World.Create/Dispose
        // costs more than everything else in that loop put together (thousands of Gen0
        // AND Gen1 collections per iteration), so such a benchmark compares world
        // construction, not entity handling. A game creates its world once.

        const int Batch = 1_000;

        [GlobalSetup(Targets = new[] { nameof(Flux_CreateDestroy), nameof(Flux_Churn), nameof(Flux_Read) })]
        public void SetupFlux()
        {
            _ctx = BenchEntityExtensions.NewContext();
            _group = _ctx.GetGroup(Matcher<Entitas.Entity>.AllOf(CompId.Position, CompId.Velocity));

            _entities = new Entitas.Entity[N];
            for (var i = 0; i < N; i++)
            {
                var e = _ctx.CreateEntity();
                e.AddPosition(i, i, i);
                e.AddVelocity(1, 1, 1);
                _entities[i] = e;
            }

            // Warm the entity and component pools, as the first seconds of a game would.
            for (var warmup = 0; warmup < 3; warmup++)
                Flux_CreateDestroy();
        }

        [GlobalSetup(Targets = new[] { nameof(Morpeh_CreateDestroy), nameof(Morpeh_Churn), nameof(Morpeh_Read) })]
        public void SetupMorpeh()
        {
            _world = Morpeh.World.Create();
            _positions = _world.GetStash<MorpehPosition>();
            _velocities = _world.GetStash<MorpehVelocity>();
            _tags = _world.GetStash<MorpehTagA>();

            _morpehEntities = new Morpeh.Entity[N];
            for (var i = 0; i < N; i++)
            {
                var e = _world.CreateEntity();
                ref var p = ref _positions.Add(e);
                p.X = i; p.Y = i; p.Z = i;
                _velocities.Add(e);
                _morpehEntities[i] = e;
            }

            _filter = _world.Filter.With<MorpehPosition>().With<MorpehVelocity>().Build();
            _world.Commit();
            _batch = new Morpeh.Entity[Batch];

            for (var warmup = 0; warmup < 3; warmup++)
                Morpeh_CreateDestroy();
        }

        [Benchmark(Baseline = true), BenchmarkCategory("CreateDestroy")]
        public void Flux_CreateDestroy()
        {
            var created = _batchFlux ??= new Entitas.Entity[Batch];
            for (var i = 0; i < Batch; i++)
            {
                var e = _ctx.CreateEntity();
                e.AddPosition(i, i, i);
                e.AddVelocity(1, 1, 1);
                created[i] = e;
            }

            for (var i = 0; i < Batch; i++)
                created[i].Destroy();
        }

        [Benchmark, BenchmarkCategory("CreateDestroy")]
        public void Morpeh_CreateDestroy()
        {
            var created = _batch;
            for (var i = 0; i < Batch; i++)
            {
                var e = _world.CreateEntity();
                ref var p = ref _positions.Add(e);
                p.X = i; p.Y = i; p.Z = i;
                ref var v = ref _velocities.Add(e);
                v.X = 1; v.Y = 1; v.Z = 1;
                created[i] = e;
            }

            for (var i = 0; i < Batch; i++)
                _world.RemoveEntity(created[i]);

            _world.Commit();
        }

        // ---- component churn while a query is watching --------------------

        [Benchmark(Baseline = true), BenchmarkCategory("Churn")]
        public void Flux_Churn()
        {
            var entities = _entities;
            for (var i = 0; i < entities.Length; i++)
            {
                var e = entities[i];
                e.AddTagA();
                e.RemoveTagA();
                e.ReplacePosition(i, i, i);
            }
        }

        [Benchmark, BenchmarkCategory("Churn")]
        public void Morpeh_Churn()
        {
            var entities = _morpehEntities;
            for (var i = 0; i < entities.Length; i++)
            {
                var e = entities[i];
                _tags.Add(e);
                _tags.Remove(e);
                ref var p = ref _positions.Get(e);
                p.X = i; p.Y = i; p.Z = i;
            }

            // Morpeh's filters catch up here; Entitas updated its groups on every change
            // above. Committing once per batch is how Morpeh is meant to be driven.
            _world.Commit();
        }

        // ---- iterate the query and read a component -----------------------

        [Benchmark(Baseline = true), BenchmarkCategory("Read")]
        public float Flux_Read()
        {
            var sum = 0f;
            var entities = _group.GetEntities();
            for (var i = 0; i < entities.Length; i++)
                sum += ((PositionComponent)entities[i].GetComponent(CompId.Position)).X;

            return sum;
        }

        [Benchmark, BenchmarkCategory("Read")]
        public float Morpeh_Read()
        {
            var sum = 0f;
            foreach (var e in _filter)
                sum += _positions.Get(e).X;

            return sum;
        }
    }
}
