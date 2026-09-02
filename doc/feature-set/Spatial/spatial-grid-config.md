---
uid: feature-spatial-spatial-grid-config
title: 'Spatial Grid Configuration & Tier Control'
description: 'One global grid, one cell size, and a per-cell simulation-tier control surface for multi-resolution worlds.'
---

# Spatial Grid Configuration & Tier Control
> One global grid, one cell size, and a per-cell simulation-tier control surface for multi-resolution worlds.

**Status:** ✅ Implemented · **Visibility:** Public · **Level:** 🔵 Core · **Category:** [Spatial](./README.md)

## 🎯 What it solves

Large worlds can't afford to simulate every entity at full frequency every tick — a 10M-entity world needs the few thousand entities near a player to run physics at 60 Hz while everything else runs coarser or not at all. Doing this with per-entity distance checks costs O(N) every frame and collapses well before six figures. Spatial Grid Configuration sets up the engine-wide coordinate grid every spatial archetype shares, and the tier control surface lets game code assign a simulation tier (full / reduced / coarse / dormant) per cell — cheaply, once per tick — instead of per entity.

## ⚙️ How it works (in brief)

`SpatialGridConfig` is computed once: world bounds and a single cell size derive the grid dimensions, and the config is handed to `DatabaseEngine.ConfigureSpatialGrid` before `InitializeArchetypes` — it cannot change afterward. All spatial archetypes share this one grid; there's no per-archetype sizing. At runtime, a `TierAssignment`-style callback system (run with `SystemPriority.High` so it executes before other systems) reads `TickContext.SpatialGrid` — an `SpatialGridAccessor` — and assigns each cell a `SimTier` flag (`Tier0`..`Tier3`). The engine consumes these per-cell tiers downstream to filter which clusters a system or query touches (see tier-filtered system dispatch in the Runtime category) — assignment itself is entirely game-owned policy; Typhon only provides storage and the helper methods below.

## 💻 Usage

```csharp
// Once, before InitializeArchetypes:
// A flat (2D) world — one cell deep on Z:
dbe.ConfigureSpatialGrid(SpatialGridConfig.Flat(
    worldMin: new Vector2(-1000f, -1000f),
    worldMax: new Vector2( 1000f,  1000f),
    cellSize: 32f));

// A volumetric world uses the constructor directly:
dbe.ConfigureSpatialGrid(new SpatialGridConfig(
    worldMin: new Vector3(-1000f, -1000f, -1000f),
    worldMax: new Vector3( 1000f,  1000f,  1000f),
    cellSize: 32f));

// Every tick, a high-priority callback system assigns tiers:
schedule.CallbackSystem("TierAssignment", ctx =>
{
    var grid = ctx.SpatialGrid;
    if (!grid.IsValid) return;

    grid.ResetAllTiers(SimTier.Tier3);               // start everyone at lowest priority

    foreach (var observer in connectedPlayers)
    {
        grid.SetTierInAABB(observer.Tier0MinX, observer.Tier0MinY, 0f,
                            observer.Tier0MaxX, observer.Tier0MaxY, 0f, SimTier.Tier0);
        grid.SetTierInAABB(observer.Tier1MinX, observer.Tier1MinY, 0f,
                            observer.Tier1MaxX, observer.Tier1MaxY, 0f, SimTier.Tier1);
    }
}, priority: SystemPriority.High);
```

| Config field | Meaning | Notes |
|---|---|---|
| `WorldMin` / `WorldMax` | World-space extent, `Vector3` | `WorldMax` is exclusive on all three axes |
| `CellSize` | Side length of one cell, world units | Must be `> 0`; one size for the whole grid |
| `MigrationHysteresisRatio` | Dead-zone fraction for migration | Default `0.05`; reserved, currently passive |

## ⚠️ Guarantees & limits

- **Set once, immutable after** — `ConfigureSpatialGrid` throws `InvalidOperationException` if called twice or after `InitializeArchetypes`.
- **One grid, one cell size, for every spatial archetype** — no per-archetype grid sizing; entity scale should be roughly uniform across archetypes sharing the grid.
- **The grid is sparse: a cell exists only once something occupies it.** A cell key is a pool slot, not a coordinate — so `SpatialGridAccessor.ComputeCellKey` / `WorldToCell` return **`-1` for an empty region**, `CellCount` counts *occupied* cells rather than the whole world, and a key must not be cached across a reopen or a rebuild (slots are handed out in creation order and renumber). Reading grid state never creates a cell; only spawning, migrating or rebuilding does. An empty region costs one absent hash entry instead of 64 bytes per cell per archetype.
- **The grid is three-dimensional; a 2D world is a grid one cell deep.** `SpatialGridConfig.Flat(Vector2, Vector2, float)` builds that shape in one call, and its cell keys are numerically identical to the ones the old two-dimensional grid produced. There is deliberately no `Vector2` overload set on the query methods — a 2D/3D pair is a call site where a `Z` gets dropped silently, which surfaces as a missing query result rather than an error.
- **Cell count must fit a 32-bit key** — `GridWidth × GridHeight × GridDepth ≤ int.MaxValue`; oversized world/cell-size combinations throw `ArgumentOutOfRangeException` at config time. (Before the grid gained a Z axis this limit was stated as a 32 768-per-axis Morton cap; Morton cell keys are gone and keys are plain row-major.)
- **`SetCellTier` requires a single-bit `SimTier` flag** — passing a combined flag (e.g. `Tier0 | Tier1`) is rejected; use the `Min` variants or per-call assignment for unions.
- **`SetCellTierMin` / `SetTierInAABB` are promote-only** — they never demote a cell already holding a higher-priority (lower-valued) tier, which is what makes the multi-observer "union of zones" pattern correct without per-observer bookkeeping.
- **All accessor methods throw `InvalidOperationException` when no grid is configured** — check `SpatialGridAccessor.IsValid` first, especially in non-spatial engines or during shutdown.
- **One tick of staleness** — tier assignment runs once per tick (before other systems); cells crossing a tier boundary mid-tick are dispatched at their prior tier until the next tick.
- **Tier filtering itself lives downstream** — this feature only assigns and exposes tiers; consuming them to filter system/query dispatch is a separate mechanism (rules `TI-01`..`TI-03`).

## 🧪 Tests

- [SpatialGridTests](https://github.com/Log2n-io/Typhon/blob/main/test/Typhon.Engine.Tests/Data/SpatialGrid/SpatialGridTests.cs) — grid dimension derivation, `WorldToCellKey`/`CellKeyToCoords` round-trips, `SetCellTier` single-bit validation, cell-count overflow throws, flat-vs-cubic Z behaviour
- [VdbSpatialGridTests](https://github.com/Log2n-io/Typhon/blob/main/test/Typhon.Engine.Tests/Data/SpatialGrid/VdbSpatialGridTests.cs) — differential against a dense reference oracle, neighbour lookups across absent blocks, concurrent cell creation, memory at 80 % empty, intra-block fill
- [CheckerboardTests](https://github.com/Log2n-io/Typhon/blob/main/test/Typhon.Engine.Tests/Runtime/CheckerboardTests.cs) — `SetCellTierMin_OnlyPromotes`, `ResetAllTiers_BulkSetsAllCells`, `SetTierInAABB_MinSemantics`, `SpatialGridAccessor_AccessibleFromTickContext`/`_MultiObserver_Union` (promote-only tiering, multi-observer union)

## 🔗 Related

- Source: [src/Typhon.Engine/Spatial/public/SpatialGridConfig.cs](https://github.com/Log2n-io/Typhon/blob/main/src/Typhon.Engine/Spatial/public/SpatialGridConfig.cs)
- Source: [src/Typhon.Engine/Spatial/public/SpatialGridAccessor.cs](https://github.com/Log2n-io/Typhon/blob/main/src/Typhon.Engine/Spatial/public/SpatialGridAccessor.cs)
- Source: [src/Typhon.Engine/Ecs/public/DatabaseEngine.cs](https://github.com/Log2n-io/Typhon/blob/main/src/Typhon.Engine/Ecs/public/DatabaseEngine.cs) (`ConfigureSpatialGrid`)
- Related catalog entry: [Spatial Query API](./spatial-query-api.md) (the per-component query layer this grid complements)
- Sibling: [Tiered Simulation Dispatch](./tiered-simulation-dispatch.md) — the primary consumer of the per-cell `SimTier` this feature exposes
- Overview: [Spatial Architecture Overview](./spatial-architecture-overview.md) — this grid vs. the R-Tree's own, unrelated Layer-1 occupancy filter

<!-- Deep dive: claude/design/Spatial/spatial-grid-api.md (full public API inventory) -->
<!-- Deep dive: claude/design/Spatial/SpatialTiers/03-tier-dispatch.md (tier assignment, dispatch, amortization, dormancy) -->
<!-- ADR: claude/adr/046-spatial-tiers-architecture.md -->
<!-- Rules: rules/spatial.md (modules SC-01, TI-01..TI-03) -->
