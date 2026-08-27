```

BenchmarkDotNet v0.14.0, macOS Sequoia 15.5 (24F74) [Darwin 24.5.0]
Apple M1 Max, 1 CPU, 10 logical and 10 physical cores
.NET SDK 9.0.303
  [Host] : .NET 8.0.5 (8.0.524.21615), Arm64 RyuJIT AdvSIMD

Job=ShortRun  Toolchain=InProcessEmitToolchain  IterationCount=3  
LaunchCount=1  WarmupCount=3  

```
| Method                    | Mean        | Error      | StdDev    | Gen0      | Gen1     | Gen2     | Allocated |
|-------------------------- |------------:|-----------:|----------:|----------:|---------:|---------:|----------:|
| CreateDestroy             | 8,282.71 μs | 285.921 μs | 15.672 μs | 1203.1250 | 593.7500 | 390.6250 | 7142861 B |
| CreateDestroyNew          | 8,188.93 μs | 637.394 μs | 34.938 μs | 1203.1250 | 593.7500 | 390.6250 | 7143036 B |
| ComponentChurnWithGroups  | 1,286.19 μs | 287.769 μs | 15.774 μs |         - |        - |        - |       2 B |
| SingleGroupChurn          |   958.82 μs | 143.086 μs |  7.843 μs |         - |        - |        - |       1 B |
| MatcherMatches            |    53.24 μs |  27.781 μs |  1.523 μs |         - |        - |        - |         - |
| ComponentRead             |    32.71 μs |   2.046 μs |  0.112 μs |         - |        - |        - |         - |
| UniqueAccessorGetGroup    |   161.04 μs |  17.065 μs |  0.935 μs |         - |        - |        - |         - |
| UniqueAccessorCachedGroup |    21.43 μs |   7.581 μs |  0.416 μs |         - |        - |        - |         - |
| EntityIndexChurn          | 2,038.94 μs | 171.324 μs |  9.391 μs |         - |        - |        - |       4 B |
