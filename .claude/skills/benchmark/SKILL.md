---
name: benchmark
description: Run regression benchmarks, track results, and generate trend reports
argument-hint: [--quick] [--report-only] [--list]
---

# Benchmark Regression Tracking

Run Typhon regression benchmarks, record results to history, and generate trend reports with regression detection.

**Comparison mode:** Each run is compared against the **immediately previous run** (not an averaged baseline). This gives a clear trend view.

**Noise filtering:** Benchmarks are automatically classified as "noisy" (filtered from regressions) when:
- Mean is below `min_measurable_ns` (default: 1.0ns) — below BDN's measurement resolution
- Coefficient of Variation exceeds `max_cov_pct` (default: 30%) — inherently high-variance benchmarks
- Absolute delta is below `min_delta_ns` (default: 0.5ns) — sub-ns shifts on fast micro-benchmarks

## Input

$ARGUMENTS may contain:
- `--quick` — Run with reduced warmup/iterations for fast feedback
- `--report-only` — Skip benchmark execution, regenerate reports from existing history
- `--list` — List all regression-tracked benchmarks with their thresholds
- (empty) — Full benchmark run + report generation

## Workflow

### `/benchmark --list`

List all regression-tracked benchmark classes and methods:

```bash
cd test/Typhon.Benchmark && dotnet run -c Release -- --list --allCategories Regression
```

Then read `benchmark/config.json` and display the configured thresholds per benchmark.

### `/benchmark --report-only`

Skip benchmark execution. Regenerate reports from existing history:

```bash
python3 benchmark/scripts/report_generator.py --history benchmark/history/results.jsonl --config benchmark/config.json --output-dir benchmark/reports
```

Read `benchmark/reports/latest.md` and display a condensed summary.

### `/benchmark --quick`

Same as default workflow below, but append quick-mode flags to BDN.

**Clean stale artifacts first** (same as Step 2 of default workflow), then run **in the background** (`run_in_background: true`) and poll with `TaskOutput` (`block: true, timeout: 600000`):

```bash
cd test/Typhon.Benchmark && dotnet run -c Release -- --allCategories Regression --exporters json --warmupCount 1 --iterationCount 2
```

Then continue with report generation step.

### `/benchmark` (default — full run + report)

#### Step 1: Build in Release

```bash
dotnet build -c Release test/Typhon.Benchmark/Typhon.Benchmark.csproj
```

If build fails, report errors and stop.

#### Step 2: Clean Stale BDN Artifacts

Remove prior BDN result files to prevent exploratory benchmark data from polluting the regression report:

```bash
# Windows
if exist "test\Typhon.Benchmark\BenchmarkDotNet.Artifacts\results" rmdir /s /q "test\Typhon.Benchmark\BenchmarkDotNet.Artifacts\results"
```
```bash
# Unix/macOS
rm -rf test/Typhon.Benchmark/BenchmarkDotNet.Artifacts/results
```

#### Step 3: Run Regression Benchmarks

```bash
cd test/Typhon.Benchmark && dotnet run -c Release --no-build -- --allCategories Regression --exporters json
```

**IMPORTANT:** This step can take up to ~12 minutes, which exceeds the Bash tool's 10-minute max timeout. Run this command **in the background** (`run_in_background: true`) and poll with `TaskOutput` (use `block: true, timeout: 600000`). Let the user know benchmarks are running before starting the background task.

#### Step 4: Generate Report

```bash
python3 benchmark/scripts/report_generator.py --bdn-results test/Typhon.Benchmark/BenchmarkDotNet.Artifacts/results --history benchmark/history/results.jsonl --config benchmark/config.json --output-dir benchmark/reports
```

#### Step 5: Display Summary

Read `benchmark/reports/latest.md` and display a condensed summary to the user:

- Total benchmarks run
- **Regressions found** (list each with name + % change) — highlight prominently
- **Improvements found** (list each with name + % change)
- Stable benchmark count
- Link to full report: `benchmark/reports/latest.md`

#### Step 6: Prompt for History Commit

Ask the user:

**Question:** "Benchmark results have been appended to history. Commit the updated history?"
**Header:** "Commit"
**Options:**
- `Yes, commit history` (description: "Commit benchmark/history/results.jsonl with the new run data")
- `No, skip commit` (description: "Keep the local changes without committing")

If yes, commit only `benchmark/history/results.jsonl`:
```bash
git add benchmark/history/results.jsonl
git commit -m "benchmark: record regression benchmark results"
```
