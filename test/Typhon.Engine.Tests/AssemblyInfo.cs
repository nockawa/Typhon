using NUnit.Framework;

// Enable parallel test execution at the fixture (class) level.
// Tests within a single class run sequentially, but different test classes run concurrently.
// This preserves any intra-class ordering ([Order] attributes) while parallelizing across classes.
[assembly: Parallelizable(ParallelScope.Fixtures)]

// Note: LevelOfParallelism requires a compile-time constant.
// For dynamic configuration (e.g., ProcessorCount / 2), use the .runsettings file or the NUnit.NumberOfTestWorkers environment variable.
// 8 workers balances speed with test stability. The JIT warmup fixture (AssemblyWarmup.cs) pre-compiles hot code paths before parallel execution begins,
// preventing timeout failures from cold JIT in the first batch.
[assembly: LevelOfParallelism(8)]
