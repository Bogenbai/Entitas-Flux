```

BenchmarkDotNet v0.14.0, macOS Sequoia 15.5 (24F74) [Darwin 24.5.0]
Apple M1 Max, 1 CPU, 10 logical and 10 physical cores
.NET SDK 9.0.303
  [Host] : .NET 8.0.5 (8.0.524.21615), Arm64 RyuJIT AdvSIMD

Job=ShortRun  Toolchain=InProcessEmitToolchain  IterationCount=3  
LaunchCount=1  WarmupCount=3  

```
| Method                    | Mean        | Error        | StdDev     | Gen0     | Gen1     | Gen2     | Allocated |
|-------------------------- |------------:|-------------:|-----------:|---------:|---------:|---------:|----------:|
| CreateDestroy             | 4,614.26 μs | 3,195.360 μs | 175.148 μs | 890.6250 | 539.0625 | 359.3750 | 5302656 B |
| CreateDestroyNew          | 4,746.56 μs |   753.003 μs |  41.275 μs | 921.8750 | 632.8125 | 273.4375 | 5302164 B |
| ComponentChurnWithGroups  |   891.11 μs |   144.685 μs |   7.931 μs |        - |        - |        - |       1 B |
| SingleGroupChurn          |   533.20 μs |   218.391 μs |  11.971 μs |        - |        - |        - |       1 B |
| MatcherMatches            |    44.84 μs |    25.519 μs |   1.399 μs |        - |        - |        - |         - |
| ComponentRead             |    34.24 μs |     4.474 μs |   0.245 μs |        - |        - |        - |         - |
| UniqueAccessorGetGroup    |   166.92 μs |    13.302 μs |   0.729 μs |        - |        - |        - |         - |
| UniqueAccessorCachedGroup |    21.38 μs |     4.276 μs |   0.234 μs |        - |        - |        - |         - |
| EntityIndexChurn          | 1,101.90 μs |    32.343 μs |   1.773 μs |        - |        - |        - |       2 B |
