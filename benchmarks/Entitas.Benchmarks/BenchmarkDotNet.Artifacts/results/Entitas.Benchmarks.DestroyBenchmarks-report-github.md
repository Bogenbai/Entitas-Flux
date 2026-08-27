```

BenchmarkDotNet v0.14.0, macOS Sequoia 15.5 (24F74) [Darwin 24.5.0]
Apple M1 Max, 1 CPU, 10 logical and 10 physical cores
.NET SDK 9.0.303
  [Host] : .NET 8.0.5 (8.0.524.21615), Arm64 RyuJIT AdvSIMD

Job=ShortRun  Toolchain=InProcessEmitToolchain  IterationCount=3  
LaunchCount=1  WarmupCount=3  

```
| Method                   | Mean     | Error    | StdDev   | Allocated |
|------------------------- |---------:|---------:|---------:|----------:|
| CreateDestroySparseChurn | 396.7 μs | 203.3 μs | 11.14 μs |         - |
