using System.Collections.Generic;
using BenchmarkDotNet.Attributes;

namespace Entitas.Benchmarks
{
    /// <summary>
    /// What does reading a group cost, per way of reading it? A frame reads the same group
    /// from many systems, and the group either kept its members since the last read or
    /// did not — the answer differs by orders of magnitude between the two.
    ///
    /// GetEntities() caches its snapshot until the membership changes, so on a stable
    /// group it is a field read; on a changed one it allocates the snapshot. The buffered
    /// overload never allocates but has no cache: it copies on every call. foreach goes
    /// over the storage directly and neither copies nor allocates, but is not a snapshot.
    ///
    /// Each benchmark performs <see cref="Reads"/> reads of a group of <see cref="N"/>
    /// entities; the "Changed" variants flip one entity's membership before every read.
    /// </summary>
    [Config(typeof(FastConfig))]
    public class GroupReadBenchmarks
    {
        const int N = 10_000;
        const int Reads = 100;

        Context<Entity> _ctx;
        Group<Entity> _group;
        Entity _flipper;
        readonly List<Entity> _buffer = new List<Entity>(16);

        [GlobalSetup]
        public void Setup()
        {
            _ctx = BenchEntityExtensions.NewContext();
            _group = (Group<Entity>)_ctx.GetGroup(Matcher<Entity>.AllOf(CompId.Position));
            for (var i = 0; i < N; i++)
            {
                var e = _ctx.CreateEntity();
                e.AddPosition(i, i, i);
                e.AddVelocity(1, 1, 1);
            }

            // Its Position is toggled to invalidate the group's caches between reads.
            _flipper = _ctx.CreateEntity();
            _flipper.AddVelocity(1, 1, 1);
        }

        void Flip()
        {
            if (_flipper.HasComponent(CompId.Position))
                _flipper.RemoveComponent(CompId.Position);
            else
                _flipper.AddPosition(0, 0, 0);
        }

        static int Sum(Entity[] entities)
        {
            var sum = 0;
            for (var i = 0; i < entities.Length; i++)
                sum += entities[i].creationIndex;
            return sum;
        }

        static int Sum(List<Entity> entities)
        {
            var sum = 0;
            for (var i = 0; i < entities.Count; i++)
                sum += entities[i].creationIndex;
            return sum;
        }

        // ---- stable group: nothing changed since the last read --------------------

        [Benchmark(Baseline = true)]
        public int Foreach_Stable()
        {
            var sum = 0;
            for (var r = 0; r < Reads; r++)
                foreach (var e in _group)
                    sum += e.creationIndex;
            return sum;
        }

        [Benchmark]
        public int GetEntities_Stable()
        {
            var sum = 0;
            for (var r = 0; r < Reads; r++)
                sum += Sum(_group.GetEntities());
            return sum;
        }

        [Benchmark]
        public int GetEntitiesBuffer_Stable()
        {
            var sum = 0;
            for (var r = 0; r < Reads; r++)
                sum += Sum(_group.GetEntities(_buffer));
            return sum;
        }

        // ---- changed group: one membership flip before every read ------------------

        [Benchmark]
        public int Foreach_Changed()
        {
            var sum = 0;
            for (var r = 0; r < Reads; r++)
            {
                Flip();
                foreach (var e in _group)
                    sum += e.creationIndex;
            }

            return sum;
        }

        [Benchmark]
        public int GetEntities_Changed()
        {
            var sum = 0;
            for (var r = 0; r < Reads; r++)
            {
                Flip();
                sum += Sum(_group.GetEntities());
            }

            return sum;
        }

        [Benchmark]
        public int GetEntitiesBuffer_Changed()
        {
            var sum = 0;
            for (var r = 0; r < Reads; r++)
            {
                Flip();
                sum += Sum(_group.GetEntities(_buffer));
            }

            return sum;
        }
    }
}
