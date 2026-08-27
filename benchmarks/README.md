# Benchmarks

Runtime benchmarks for the Entitas-Flux ECS core, on [BenchmarkDotNet](https://benchmarkdotnet.org).

They exist so the next optimization has something to be compared against: v0.0.6 shipped
a round of hot-path work (component-index tracking, group updates, entity/matcher paths)
whose effect can no longer be reproduced, because the numbers behind it were never
committed.

## Running them

```bash
# everything (minutes)
dotnet run -c Release --project benchmarks/Entitas.Benchmarks

# one class, e.g. entity destruction
dotnet run -c Release --project benchmarks/Entitas.Benchmarks -- --filter '*DestroyBenchmarks*'

# smoke run: executes every benchmark once, no statistics — for checking they still work
dotnet run -c Release --project benchmarks/Entitas.Benchmarks -- --filter '*' --job Dry
```

Results land in `BenchmarkDotNet.Artifacts/` (git-ignored). Attach the markdown report to
the PR when a change is meant to affect performance.

## Why CI only builds them

Shared CI runners share their CPU, so wall-clock numbers from them are noise — comparing
two such runs would produce confident-looking nonsense. CI therefore only builds the
project, which is enough to catch a benchmark that stopped compiling after a refactor.
Measure on a quiet machine, compare against a baseline from the same machine.

The project targets `net8.0` and is deliberately outside `Entitas.sln`: it must not
affect the framework build, and the framework's `net6.0` target should not hold the
benchmarks back.
