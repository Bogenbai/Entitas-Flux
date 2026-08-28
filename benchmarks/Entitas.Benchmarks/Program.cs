using System;
using System.Diagnostics;
using BenchmarkDotNet.Running;

namespace Entitas.Benchmarks
{
    public static class Program
    {
        public static void Main(string[] args)
        {
            // A profiling target: the churn workload on a loop, with no BenchmarkDotNet
            // harness in the trace. Run it under dotnet-trace to see where the time in a
            // component change actually goes.
            if (args.Length > 0 && args[0] == "profile-churn")
            {
                ProfileChurn(rounds: args.Length > 1 ? int.Parse(args[1]) : 12_000);
                return;
            }

            BenchmarkSwitcher.FromAssembly(typeof(Program).Assembly).Run(args);
        }

        static void ProfileChurn(int rounds)
        {
            const int N = 10_000;

            var ctx = BenchEntityExtensions.NewContext();
            ctx.GetGroup(Matcher<Entity>.AllOf(CompId.Position, CompId.Velocity));
            ctx.GetGroup(Matcher<Entity>.AllOf(CompId.Health));
            ctx.GetGroup(((IAllOfMatcher<Entity>)Matcher<Entity>.AllOf(CompId.Position)).NoneOf(CompId.TagA));

            var entities = new Entity[N];
            for (var i = 0; i < N; i++)
            {
                var e = ctx.CreateEntity();
                e.AddPosition(i, i, i);
                e.AddVelocity(1, 1, 1);
                e.AddHealth(100);
                entities[i] = e;
            }

            // A fixed round count rather than a stopwatch: querying the clock inside the
            // loop would show up in the profile as if it were part of the workload.
            Console.WriteLine($"profiling {rounds} rounds over {N} entities, 3 groups");
            for (var round = 0; round < rounds; round++)
            {
                for (var i = 0; i < N; i++)
                {
                    var e = entities[i];
                    e.AddTagA();
                    e.RemoveTagA();
                    e.ReplacePosition(i, i, i);
                }
            }

            Console.WriteLine("done");
        }
    }
}
