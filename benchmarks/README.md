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

## Comparing against upstream Entitas

The fork changed the runtime's hot paths (component-index tracking, group updates,
ref-counting in entity indices). Whether that paid off is a question for a measurement,
not an opinion:

```bash
./benchmarks/compare-with-upstream.sh                       # core benchmarks
./benchmarks/compare-with-upstream.sh '*DestroyBenchmarks*' # one class
```

The script runs the same benchmark code twice — once compiled against this repo, once
against the `Entitas` 1.14.1 NuGet package it forked from (`-p:UseUpstream=true`) — and
prints them side by side. Two builds rather than one process, because both ship an
assembly literally called `Entitas.dll` and they cannot coexist in one output folder.
The benchmark sources need no `#if`: the API they use is identical in both.

As measured on an Apple M1 Max (macOS 15.5, .NET 8, ShortRun):

| Benchmark | upstream | flux | time | allocations |
| --- | ---: | ---: | ---: | ---: |
| CreateDestroy | 8,283 μs | 4,614 μs | **0.56x** | **0.74x** |
| ComponentChurnWithGroups | 1,286 μs | 891 μs | **0.69x** | 0.50x |
| SingleGroupChurn | 959 μs | 533 μs | **0.56x** | 1.00x |
| EntityIndexChurn | 2,039 μs | 1,102 μs | **0.54x** | 0.50x |
| MatcherMatches | 53.2 μs | 44.8 μs | 0.84x | — |
| ComponentRead | 32.7 μs | 34.2 μs | 1.05x | — |
| UniqueAccessorCachedGroup | 21.4 μs | 21.4 μs | 1.00x | — |

Below 1.00x means the fork wins. Entity churn and entity-index churn are roughly twice
as fast and allocate a quarter less; the read paths are unchanged within noise.

**Read the allocation column first.** Allocations are deterministic — the same numbers on
any machine — while timings on a laptop with other work running moved by more than 20%
between two runs of the same build here. A single run tells you about a 2x difference,
not about a 5% one.

## Why CI only builds them

Shared CI runners share their CPU, so wall-clock numbers from them are noise — comparing
two such runs would produce confident-looking nonsense. CI therefore only builds the
project, which is enough to catch a benchmark that stopped compiling after a refactor.
Measure on a quiet machine, compare against a baseline from the same machine.

The project targets `net8.0` and is deliberately outside `Entitas.sln`: it must not
affect the framework build, and the framework's `net6.0` target should not hold the
benchmarks back.
