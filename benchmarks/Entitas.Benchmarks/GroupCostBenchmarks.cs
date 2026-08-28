using BenchmarkDotNet.Attributes;

namespace Entitas.Benchmarks
{
    /// <summary>
    /// Where does Entitas spend its time on component churn? Same work, only the number
    /// of registered groups changes. Entitas updates groups eagerly on every add/remove,
    /// so if group maintenance dominates, this scales with the group count — and that is
    /// the thing worth optimizing, not the component plumbing.
    /// </summary>
    [Config(typeof(FastConfig))]
    public class GroupCostBenchmarks
    {
        const int N = 10_000;

        Context<Entity> _none;
        Context<Entity> _one;
        Context<Entity> _three;
        Entity[] _noneEntities;
        Entity[] _oneEntities;
        Entity[] _threeEntities;

        [GlobalSetup]
        public void Setup()
        {
            _none = BenchEntityExtensions.NewContext();
            _noneEntities = Fill(_none);

            _one = BenchEntityExtensions.NewContext();
            _one.GetGroup(Matcher<Entity>.AllOf(CompId.Position, CompId.Velocity));
            _oneEntities = Fill(_one);

            _three = BenchEntityExtensions.NewContext();
            _three.GetGroup(Matcher<Entity>.AllOf(CompId.Position, CompId.Velocity));
            _three.GetGroup(Matcher<Entity>.AllOf(CompId.Health));
            _three.GetGroup(((IAllOfMatcher<Entity>)Matcher<Entity>.AllOf(CompId.Position)).NoneOf(CompId.TagA));
            _threeEntities = Fill(_three);
        }

        static Entity[] Fill(Context<Entity> ctx)
        {
            var entities = new Entity[N];
            for (var i = 0; i < N; i++)
            {
                var e = ctx.CreateEntity();
                e.AddPosition(i, i, i);
                e.AddVelocity(1, 1, 1);
                e.AddHealth(100);
                entities[i] = e;
            }

            return entities;
        }

        static void Churn(Entity[] entities)
        {
            for (var i = 0; i < entities.Length; i++)
            {
                var e = entities[i];
                e.AddTagA();
                e.RemoveTagA();
                e.ReplacePosition(i, i, i);
            }
        }

        [Benchmark(Baseline = true)] public void Churn_NoGroups() => Churn(_noneEntities);
        [Benchmark] public void Churn_OneGroup() => Churn(_oneEntities);
        [Benchmark] public void Churn_ThreeGroups() => Churn(_threeEntities);
    }
}

namespace Entitas.Benchmarks
{
    /// <summary>
    /// Splits "a group was consulted" from "a group changed its membership". The first is
    /// a matcher evaluation; the second is a HashSet add/remove plus cache invalidation
    /// plus two events. Which of them dominates decides what is worth optimizing.
    /// </summary>
    [Config(typeof(FastConfig))]
    public class GroupMembershipBenchmarks
    {
        const int N = 10_000;

        Context<Entity> _consulted;
        Context<Entity> _flipping;
        Entity[] _consultedEntities;
        Entity[] _flippingEntities;

        [GlobalSetup]
        public void Setup()
        {
            // Watches Position; replacing Position consults it, membership never changes.
            _consulted = BenchEntityExtensions.NewContext();
            _consulted.GetGroup(Matcher<Entity>.AllOf(CompId.Position));
            _consultedEntities = Fill(_consulted);

            // Watches TagA through NoneOf; adding and removing TagA flips membership.
            _flipping = BenchEntityExtensions.NewContext();
            _flipping.GetGroup(((IAllOfMatcher<Entity>)Matcher<Entity>.AllOf(CompId.Position)).NoneOf(CompId.TagA));
            _flippingEntities = Fill(_flipping);
        }

        static Entity[] Fill(Context<Entity> ctx)
        {
            var entities = new Entity[N];
            for (var i = 0; i < N; i++)
            {
                var e = ctx.CreateEntity();
                e.AddPosition(i, i, i);
                e.AddVelocity(1, 1, 1);
                entities[i] = e;
            }

            return entities;
        }

        [Benchmark(Baseline = true)]
        public void GroupConsulted_MembershipStable()
        {
            var entities = _consultedEntities;
            for (var i = 0; i < entities.Length; i++)
                entities[i].ReplacePosition(i, i, i);
        }

        [Benchmark]
        public void GroupMembershipFlips()
        {
            var entities = _flippingEntities;
            for (var i = 0; i < entities.Length; i++)
            {
                entities[i].AddTagA();      // leaves the group
                entities[i].RemoveTagA();   // re-enters it
            }
        }
    }
}
