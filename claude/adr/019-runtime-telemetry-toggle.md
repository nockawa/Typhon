# ADR-019: Runtime Telemetry Toggle via Static Readonly

**Status**: Accepted
**Date**: 2025-01 (inferred from telemetry branch work)
**Deciders**: Developer + Claude (design session)

## Context

Typhon's telemetry (lock contention tracking, operation timing, cache hit ratios) was initially implemented with compile-time `#if TELEMETRY` directives. This requires:

1. Separate build configurations (Debug, Release, Telemetry)
2. Different binaries for instrumented vs production
3. Cannot enable telemetry in production without redeployment

Goal: Single binary that can enable/disable telemetry at startup with zero overhead when disabled.

## Decision

Use **`static readonly` fields** set once at startup, enabling JIT dead-code elimination:

```csharp
public static class TelemetryConfig
{
    // Set once at startup, never changed. JIT treats as constants.
    public static readonly bool MetricsEnabled;
    public static readonly bool TracingEnabled;
    public static readonly bool VerboseLoggingEnabled;
}

// Usage: JIT eliminates entire block when MetricsEnabled == false
if (TelemetryConfig.MetricsEnabled)
{
    _txCounter.Add(1);
    _txDuration.Record(elapsed);
}
```

**Key JIT optimization:** When a `static readonly` field is `false`, the JIT compiler can prove it will never change (initialized before any code runs). The entire `if` block becomes dead code and is eliminated — zero overhead in the hot path.

## Alternatives Considered

1. **`#if TELEMETRY` compile-time** (current state) — Zero overhead when disabled, but requires separate binary. Cannot diagnose production issues without redeployment.
2. **Interface-based (ITelemetryProvider)** — Runtime flexibility, but virtual dispatch on every call (~2–5ns). Unacceptable in hot paths called millions of times.
3. **`volatile bool` field** — Runtime toggle, but volatile prevents JIT optimization. Memory barrier on every check.
4. **Regular `static` field (not readonly)** — JIT cannot prove value won't change; cannot eliminate dead code. Branch on every check.
5. **ConditionalAttribute** — Compile-time only; same problem as `#if`.

## Consequences

**Positive:**
- Single binary for all environments (dev, staging, production)
- Zero overhead when disabled (JIT dead-code elimination proven by benchmarks)
- Can enable telemetry in production at startup for diagnostics
- No interface dispatch overhead
- Simple implementation (just `if` statements, natural code flow)

**Negative:**
- Cannot toggle telemetry after startup (requires process restart)
- JIT optimization is an implementation detail (not contractual — though stable in .NET 6+)
- Slightly slower first-JIT compilation when telemetry enabled (more code to compile)
- All telemetry code remains in IL (larger assembly, though JIT skips it)

**Cross-references:**
- [08-observability.md](../overview/08-observability.md) §8.5 — Telemetry configuration
- [01-concurrency.md](../overview/01-concurrency.md) — AccessControl telemetry integration
- `src/Typhon.Engine/Misc/AccessControl/AccessControl.Telemetry.cs` — Example usage
