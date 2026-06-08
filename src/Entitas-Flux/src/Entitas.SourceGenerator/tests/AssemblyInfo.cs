// The source generator carries a small amount of process-global state (the
// [ThreadStatic] CodeGeneratorExtensions.ignoreNamespaces, set per Execute). xUnit
// parallelizes test classes across threads and CSharpGeneratorDriver may run the
// generator on pooled threads, which can interleave runs that expect different
// ignoreNamespaces values. Run this assembly's tests sequentially for determinism.
// (This is a test-harness concern only; in Unity each assembly compiles on its own
// thread where [ThreadStatic] already isolates the flag.)
[assembly: Xunit.CollectionBehavior(DisableTestParallelization = true)]
