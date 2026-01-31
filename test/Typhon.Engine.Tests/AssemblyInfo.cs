using NUnit.Framework;

// Enable parallel test execution at the fixture (class) level.
// Tests within a single class run sequentially, but different test classes run concurrently.
// This preserves any intra-class ordering ([Order] attributes) while parallelizing across classes.
[assembly: Parallelizable(ParallelScope.Fixtures)]

// Note: LevelOfParallelism requires a compile-time constant.
// For dynamic configuration (e.g., ProcessorCount / 2), use the .runsettings file
// or the NUnit.NumberOfTestWorkers environment variable.
// 8 workers balances speed with test stability (some timing-sensitive concurrency
// tests become flaky at higher parallelism due to CPU contention).
[assembly: LevelOfParallelism(8)]
