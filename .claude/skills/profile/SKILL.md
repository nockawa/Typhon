---
name: profile
description: Profile a benchmark workload with dotTrace sampling, generate an XML report, and analyze hot spots
argument-hint: <target> [--compare <baseline.xml>] [--filter <pattern>] [--callers <method>] [--top N]
---

# dotTrace Profiling

Profile a Typhon benchmark workload using JetBrains dotTrace (sampling mode), export results via Reporter, and analyze with the Python script.

## Prerequisites

These tools must be installed (one-time setup):

- `dotnet tool install --global JetBrains.dotTrace.GlobalTools`
- `dotnet add package JetBrains.dotTrace.CommandLineTools.windows-x64` (for Reporter.exe)

Both are already installed in this project.

## Tool Paths

```
DOTTRACE = dottrace (global tool, requires DOTNET_ROLL_FORWARD=LatestMajor on .NET 10+)
REPORTER = $USERPROFILE/.nuget/packages/jetbrains.dottrace.commandlinetools.windows-x64/2025.3.2/tools/Reporter.exe
ANALYZER = test/Typhon.Benchmark/profiling/analyze_profile.py
PROFILE_DIR = test/Typhon.Benchmark/profiling
```

If `REPORTER` path doesn't exist, look for the latest version:
```bash
ls "$USERPROFILE/.nuget/packages/jetbrains.dottrace.commandlinetools.windows-x64/"
```

## Input

$ARGUMENTS may contain:
- `<target>` (required) — The benchmark target to profile. Can be:
  - A named profile target: `--profile-delete` (BTreeDeleteProfile)
  - A BDN filter: `--filter *BTreeMicro*`
  - A custom exe command to profile
- `--compare <baseline.xml>` — Compare current results against a baseline profile XML
- `--filter <pattern>` — Regex pattern to filter the analysis output (e.g., `Remove`, `BTree`)
- `--callers <method>` — Show callers of a specific method
- `--top N` — Number of top functions to show (default: 15)
- `--save-as <name>` — Save the snapshot/report with a descriptive name (default: `btree-profile`)

## Workflow

### Step 1: Build Release

```bash
cd test/Typhon.Benchmark && dotnet build -c Release
```

If build fails, report errors and stop.

### Step 2: Profile with dotTrace

Determine the profiling command based on `<target>`:

**For `--profile-delete` or other custom flags:**
```bash
cd test/Typhon.Benchmark && DOTNET_ROLL_FORWARD=LatestMajor dottrace start \
  --profiling-type=Sampling \
  --save-to=profiling/<name>.dtp \
  --overwrite \
  --propagate-exit-code \
  -- bin/Release/net10.0/Typhon.Benchmark.exe <target-args>
```

**For BDN `--filter` targets:**
```bash
cd test/Typhon.Benchmark && DOTNET_ROLL_FORWARD=LatestMajor dottrace start \
  --profiling-type=Sampling \
  --save-to=profiling/<name>.dtp \
  --overwrite \
  --propagate-exit-code \
  -- bin/Release/net10.0/Typhon.Benchmark.exe --filter '<filter>'
```

**IMPORTANT:** Always profile the **built executable directly** (`bin/Release/net10.0/Typhon.Benchmark.exe`), NOT via `dotnet run`. Using `dotnet run` adds MSBuild noise to the profile.

**IMPORTANT:** Always use `DOTNET_ROLL_FORWARD=LatestMajor` — the dotTrace tool targets .NET 8 but we run .NET 10+.

### Step 3: Generate XML Report

Use the pattern file to extract Typhon-only functions:

```bash
"$USERPROFILE/.nuget/packages/jetbrains.dottrace.commandlinetools.windows-x64/2025.3.2/tools/Reporter.exe" \
  report "profiling/<name>.dtp" \
  --pattern="profiling/btree-pattern.xml" \
  --save-to="profiling/<name>-report.xml" \
  --overwrite \
  --save-signature
```

The default pattern file (`profiling/btree-pattern.xml`) captures all `Typhon\.` methods. For other workloads, create a specific pattern file:

```xml
<Patterns>
  <Pattern PrintCallstacks="Full">Typhon\.</Pattern>
</Patterns>
```

### Step 4: Analyze Results

**Default analysis (full report):**
```bash
cd test/Typhon.Benchmark && python profiling/analyze_profile.py profiling/<name>-report.xml
```

**With filter:**
```bash
python profiling/analyze_profile.py profiling/<name>-report.xml --filter "Remove" --top 20
```

**Callers analysis:**
```bash
python profiling/analyze_profile.py profiling/<name>-report.xml --callers "GetChunk"
```

**JSON output (for programmatic use):**
```bash
python profiling/analyze_profile.py profiling/<name>-report.xml --json
```

### Step 5: Comparison (if `--compare` was specified)

If a baseline XML was provided, run the comparison:

```bash
python profiling/analyze_profile.py profiling/<baseline>.xml --compare profiling/<name>-report.xml
```

This shows per-function regressions and improvements between the two profiles.

### Step 6: Display Summary

Present the results to the user:

1. **Top 10 hot spots by self time** — where CPU time is actually spent
2. **Operation breakdown** — time split by BTree operation (Add/Remove/TryGet)
3. **Key observations** — any surprising hot spots or optimization opportunities
4. If comparison mode: **regressions and improvements**

### Example Usage

```
/profile --profile-delete
/profile --profile-delete --filter Remove
/profile --profile-delete --save-as before-opt
/profile --profile-delete --save-as after-opt --compare before-opt-report.xml
/profile --filter '*BTreeMicro*' --save-as btree-micro
```

## Notes

- Snapshot files (`.dtp`) are ~50-100MB binary files — they are gitignored
- The XML report files are also gitignored (regenerated from snapshots)
- Pattern file (`btree-pattern.xml`) and analyzer script (`analyze_profile.py`) ARE tracked in git
- Sampling mode has ~5-10ms resolution — results are statistical approximations, not exact timings
- For finer granularity, use `--profiling-type=Tracing` instead of `Sampling` (much higher overhead)
