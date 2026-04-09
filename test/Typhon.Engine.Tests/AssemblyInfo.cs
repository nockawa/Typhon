using NUnit.Framework;

// Enable parallel test execution at the fixture (class) level.
// Tests within a single class run sequentially, but different test classes run concurrently.
// This preserves any intra-class ordering ([Order] attributes) while parallelizing across classes.
[assembly: Parallelizable(ParallelScope.Fixtures)]

// Note: LevelOfParallelism requires a compile-time constant.
// For dynamic configuration (e.g., ProcessorCount / 2), use the .runsettings file or the NUnit.NumberOfTestWorkers environment variable.
// Reduced from 8 to 4 workers — 8-way parallelism surfaced order-dependent flakes
// (NTFS MFT contention on temp DB files, throughput-threshold races on shared CPU). 4 workers
// retain most of the speedup while cutting observed flake rate. Revisit after the test harness
// moves each fixture to a fully isolated temp directory. The JIT warmup fixture (AssemblyWarmup.cs)
// pre-compiles hot code paths before parallel execution begins.
[assembly: LevelOfParallelism(4)]
