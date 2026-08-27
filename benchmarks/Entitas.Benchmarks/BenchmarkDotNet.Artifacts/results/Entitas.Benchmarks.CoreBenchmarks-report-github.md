```

BenchmarkDotNet v0.14.0, macOS Sequoia 15.5 (24F74) [Darwin 24.5.0]
Apple M1 Max, 1 CPU, 10 logical and 10 physical cores
.NET SDK 9.0.303
  [Host] : .NET 8.0.5 (8.0.524.21615), Arm64 RyuJIT AdvSIMD

Job=ShortRun  Toolchain=InProcessEmitToolchain  IterationCount=3  
LaunchCount=1  WarmupCount=3  

```
| Method                    | Mean        | Error      | StdDev    | Gen0     | Gen1     | Gen2     | Allocated |
|-------------------------- |------------:|-----------:|----------:|---------:|---------:|---------:|----------:|
| CreateDestroy             | 5,277.23 μs | 905.085 μs | 49.611 μs | 882.8125 | 515.6250 | 343.7500 | 5302640 B |
| CreateDestroyNew          | 5,225.70 μs | 633.161 μs | 34.706 μs | 898.4375 | 554.6875 | 367.1875 | 5302312 B |
| ComponentChurnWithGroups  |   979.19 μs | 110.945 μs |  6.081 μs |        - |        - |        - |       1 B |
| SingleGroupChurn          |   564.92 μs | 263.413 μs | 14.439 μs |        - |        - |        - |       1 B |
| MatcherMatches            |    47.84 μs |   6.798 μs |  0.373 μs |        - |        - |        - |         - |
| ComponentRead             |    36.37 μs |   5.101 μs |  0.280 μs |        - |        - |        - |         - |
| UniqueAccessorGetGroup    |   169.50 μs |  10.117 μs |  0.555 μs |        - |        - |        - |         - |
| UniqueAccessorCachedGroup |    22.61 μs |   0.790 μs |  0.043 μs |        - |        - |        - |         - |
| EntityIndexChurn          | 1,203.58 μs |  79.175 μs |  4.340 μs |        - |        - |        - |       2 B |
