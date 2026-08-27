using System;
using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Configs;
using BenchmarkDotNet.Diagnosers;
using BenchmarkDotNet.Jobs;
using BenchmarkDotNet.Toolchains.InProcess.Emit;
using Entitas;

namespace Entitas.Benchmarks
{
    public class FastConfig : ManualConfig
    {
        public FastConfig()
        {
            AddJob(Job.ShortRun.WithToolchain(InProcessEmitToolchain.Instance));
            AddDiagnoser(MemoryDiagnoser.Default);
        }
    }

    [Config(typeof(FastConfig))]
    public class CoreBenchmarks
    {
        const int N = 10_000;

        Context<Entity> _ctx;
        Entity[] _entities;

        // ---- Create / destroy churn -------------------------------------

        [Benchmark]
        public void CreateDestroy()
        {
            var ctx = BenchEntityExtensions.NewContext();
            for (var i = 0; i < N; i++)
            {
                var e = ctx.CreateEntity();
                e.AddPosition(i, i, i);
                e.AddVelocity(1, 1, 1);
            }

            ctx.DestroyAllEntities();
        }

        // Same scenario but using the optimized generated-code path
        // (inline pool + concrete new, no Activator.CreateInstance).
        [Benchmark]
        public void CreateDestroyNew()
        {
            var ctx = BenchEntityExtensions.NewContext();
            for (var i = 0; i < N; i++)
            {
                var e = ctx.CreateEntity();
                e.AddPositionNew(i, i, i);
                e.AddVelocityNew(1, 1, 1);
            }

            ctx.DestroyAllEntities();
        }

        // ---- Component churn that drives group membership updates --------
        // (Matcher.Matches / HasComponents / HasAnyComponent hot path)

        [GlobalSetup(Target = nameof(ComponentChurnWithGroups))]
        public void SetupChurn()
        {
            _ctx = BenchEntityExtensions.NewContext();
            _ctx.GetGroup(Matcher<Entity>.AllOf(CompId.Position, CompId.Velocity));
            _ctx.GetGroup(Matcher<Entity>.AllOf(CompId.Health));
            _ctx.GetGroup(((Entitas.IAllOfMatcher<Entity>)Matcher<Entity>.AllOf(CompId.Position)).NoneOf(CompId.TagA));

            _entities = new Entity[N];
            for (var i = 0; i < N; i++)
            {
                var e = _ctx.CreateEntity();
                e.AddPosition(i, i, i);
                e.AddVelocity(1, 1, 1);
                e.AddHealth(100);
                _entities[i] = e;
            }
        }

        [Benchmark]
        public void ComponentChurnWithGroups()
        {
            var entities = _entities;
            for (var i = 0; i < entities.Length; i++)
            {
                var e = entities[i];
                e.AddTagA();            // leaves AllOf(Position).NoneOf(TagA)
                e.RemoveTagA();         // re-enters it
                e.ReplacePosition(i, i, i);
            }
        }

        // ---- Component churn where the index maps to exactly ONE group ---
        // (exercises the single-group fast path in updateGroups)

        [GlobalSetup(Target = nameof(SingleGroupChurn))]
        public void SetupSingleGroup()
        {
            _ctx = BenchEntityExtensions.NewContext();
            _ctx.GetGroup(Matcher<Entity>.AllOf(CompId.Position));
            _ctx.GetGroup(Matcher<Entity>.AllOf(CompId.Velocity));
            _ctx.GetGroup(Matcher<Entity>.AllOf(CompId.Health));
            _ctx.GetGroup(Matcher<Entity>.AllOf(CompId.TagA)); // TagA index -> exactly 1 group

            _entities = new Entity[N];
            for (var i = 0; i < N; i++)
            {
                var e = _ctx.CreateEntity();
                e.AddPosition(i, i, i);
                e.AddVelocity(1, 1, 1);
                e.AddHealth(100);
                _entities[i] = e;
            }
        }

        [Benchmark]
        public void SingleGroupChurn()
        {
            var entities = _entities;
            for (var i = 0; i < entities.Length; i++)
            {
                var e = entities[i];
                e.AddTagA();    // TagA index -> 1 group
                e.RemoveTagA();
            }
        }

        // ---- Direct Matcher.Matches -------------------------------------

        IMatcher<Entity> _matcher;

        [GlobalSetup(Target = nameof(MatcherMatches))]
        public void SetupMatcher()
        {
            _ctx = BenchEntityExtensions.NewContext();
            _matcher = ((Entitas.IAllOfMatcher<Entity>)Matcher<Entity>.AllOf(CompId.Position, CompId.Velocity)).NoneOf(CompId.TagA);
            _entities = new Entity[N];
            for (var i = 0; i < N; i++)
            {
                var e = _ctx.CreateEntity();
                e.AddPosition(i, i, i);
                e.AddVelocity(1, 1, 1);
                if ((i & 3) == 0) e.AddHealth(1);
                if ((i & 7) == 0) e.AddTagA();
                _entities[i] = e;
            }
        }

        [Benchmark]
        public int MatcherMatches()
        {
            var matcher = _matcher;
            var entities = _entities;
            var count = 0;
            for (var i = 0; i < entities.Length; i++)
                if (matcher.Matches(entities[i]))
                    count++;

            return count;
        }

        // ---- Component read through GetComponent (accessor hot path) ------

        [GlobalSetup(Target = nameof(ComponentRead))]
        public void SetupRead()
        {
            _ctx = BenchEntityExtensions.NewContext();
            _entities = new Entity[N];
            for (var i = 0; i < N; i++)
            {
                var e = _ctx.CreateEntity();
                e.AddPosition(i, i, i);
                e.AddHealth(i);
                _entities[i] = e;
            }
        }

        [Benchmark]
        public float ComponentRead()
        {
            var entities = _entities;
            float sum = 0;
            for (var i = 0; i < entities.Length; i++)
            {
                var e = entities[i];
                var p = (PositionComponent)e.GetComponent(CompId.Position);
                sum += p.X + p.Y + p.Z;
            }

            return sum;
        }

        // ---- Unique-component accessor pattern --------------------------
        // context.hasX / context.xEntity call GetGroup(matcher) on every read.

        IMatcher<Entity> _uniqueMatcher;
        IGroup<Entity> _cachedGroup;

        [GlobalSetup(Targets = new[] { nameof(UniqueAccessorGetGroup), nameof(UniqueAccessorCachedGroup) })]
        public void SetupUnique()
        {
            _ctx = BenchEntityExtensions.NewContext();
            _uniqueMatcher = Matcher<Entity>.AllOf(CompId.Health);
            _cachedGroup = _ctx.GetGroup(_uniqueMatcher);
            var e = _ctx.CreateEntity();
            e.AddHealth(1);
        }

        [Benchmark]
        public int UniqueAccessorGetGroup()
        {
            var count = 0;
            for (var i = 0; i < N; i++)
                if (_ctx.GetGroup(_uniqueMatcher).GetSingleEntity() != null)
                    count++;

            return count;
        }

        [Benchmark]
        public int UniqueAccessorCachedGroup()
        {
            var count = 0;
            var group = _cachedGroup;
            for (var i = 0; i < N; i++)
                if (group.GetSingleEntity() != null)
                    count++;

            return count;
        }

        // ---- EntityIndex add / remove churn (ref-count dictionary path) --

        EntityIndex<Entity, int> _index;

        [GlobalSetup(Target = nameof(EntityIndexChurn))]
        public void SetupIndex()
        {
            _ctx = BenchEntityExtensions.NewContext();
            var group = _ctx.GetGroup(Matcher<Entity>.AllOf(CompId.Health));
            _index = new EntityIndex<Entity, int>("health", group, BenchEntityExtensions.HealthKey);

            _entities = new Entity[N];
            for (var i = 0; i < N; i++)
            {
                var e = _ctx.CreateEntity();
                e.AddHealth(i % 100);
                _entities[i] = e;
            }
        }

        [Benchmark]
        public void EntityIndexChurn()
        {
            var entities = _entities;
            for (var i = 0; i < entities.Length; i++)
            {
                var e = entities[i];
                e.RemoveComponent(CompId.Health); // index.removeEntity
                e.AddHealth(i % 100);             // index.addEntity
            }
        }
    }
}
