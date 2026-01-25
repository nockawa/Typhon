# Part 2: Approaches Analyzed

## Approach A: Epoch Only (USN in Revisions)

**Concept**: Add 2-4 byte `UowEpoch` to each revision. Track last committed epoch in header.

```csharp
struct CompRevStorageElement  // 12-14 bytes
{
    int ComponentChunkId;
    uint _packedTickHigh;
    ushort _packedTickLow;
    ushort UowEpoch;  // NEW: +2 bytes
}
```

**Recovery**: Any revision with `Epoch > LastCommittedEpoch` → void

| Pros | Cons |
|------|------|
| Simple conceptually | Single committed epoch doesn't work for concurrent UoWs |
| No separate log | Need bitmap for concurrent epochs |
| Epoch directly in revision | Still need O(n) scan to void orphaned revisions |

**Verdict**: Partial solution. Needs bitmap/registry for concurrent UoWs.

---

## Approach B: Two-Level Visibility

**Concept**: Split visibility into soft-commit (intra-UoW) and hard-commit (durable).

```csharp
enum Visibility { Uncommitted=0, SoftCommit=1, HardCommit=2 }
```

- `Tx.Commit()` → SoftCommit (visible within same UoW)
- `UoW.Flush()` → Bulk update SoftCommit → HardCommit

**Recovery**: Treat all SoftCommit as Uncommitted (invisible)

| Pros | Cons |
|------|------|
| Elegant - crash recovery is "free" | **BROKEN**: Can't identify same-UoW without storing UowId |
| No separate registry | Flush requires O(n) updates to all revisions |
| Minimal storage overhead | Defeats purpose if we add UowId anyway |

**Verdict**: Cannot work without storing UoW identity in revision.

---

## Approach C: Hybrid (Registry + Epoch)

**Concept**: Combine UoW Registry (small tracking structure) with Epoch in revisions.

```
Registry Segment (small, ~24KB):
┌──────────┬────────────┬─────────┬─────────┐
│ Epoch    │ Status     │ MinTSN  │ MaxTSN  │
├──────────┼────────────┼─────────┼─────────┤
│ 1001     │ Pending    │ 5000    │ 5010    │
│ 1002     │ Committed  │ 5011    │ 5025    │
└──────────┴────────────┴─────────┴─────────┘

Revisions (12 bytes each):
[..existing fields..][UowEpoch=1001]
```

**Recovery**: Scan registry → void all Pending epochs → done

| Pros | Cons |
|------|------|
| O(1) recovery | +2 bytes per revision |
| Intra-UoW visibility works | Two fsync points (data, then registry) |
| Scales to 4096+ concurrent UoWs | Small registry overhead (~24KB) |
| Fast read path (3 compares) | Registry corruption requires fallback |

**Verdict**: Best balance of all requirements.

---

## Approach D: Mini-WAL

**Concept**: Write-ahead log for UoW boundaries only.

```
UoW Log File:
[BEGIN 1001][BEGIN 1002][COMMIT 1001][BEGIN 1003]...
```

**Recovery**: Any BEGIN without COMMIT → incomplete UoW

| Pros | Cons |
|------|------|
| Industry standard | Additional file I/O |
| Supports point-in-time recovery | Log management (truncation, rotation) |
| Clear durability semantics | Must fsync log BEFORE data (ordering) |

**Verdict**: More complexity than needed for UoW-level recovery.

---

## Approach E: Epoch Ranges

**Concept**: Track committed epoch ranges in header instead of individual entries.

```
Header:
  CommittedRanges: [(1, 998), (1000, 1000)]
  // 999 incomplete, 1000 committed, 1001+ incomplete
```

| Pros | Cons |
|------|------|
| Very compact | Breaks down with concurrent UoWs |
| Single header update | Range gaps require complex tracking |

**Verdict**: Only works if UoWs commit roughly in order. Not suitable for web server workloads.

---

## Comparison Matrix

| Criterion | A (Epoch) | B (Two-level) | **C (Hybrid)** | D (WAL) | E (Ranges) |
|-----------|-----------|---------------|----------------|---------|------------|
| Per-revision cost | +2 bytes | 0 | **+2 bytes** | 0 | +2 bytes |
| Global overhead | 64 bytes | 0 | **24KB** | Log file | 16 bytes |
| Intra-UoW visibility | ✅ | ❌ | **✅** | ✅ | ✅ |
| Concurrent UoW support | Limited | N/A | **4096+** | Unlimited | Limited |
| Recovery complexity | O(n) | O(1) | **O(1)** | O(log) | O(1) |
| Read path overhead | Medium | Low | **Low** | None | Low |

---

## Winner: Approach C (Hybrid)

The hybrid approach provides:
- Fast recovery (scan small registry, not entire database)
- Proper intra-UoW visibility (epoch identifies same-UoW)
- Acceptable per-revision overhead (+2 bytes)
- Scales to high concurrency (4096+ UoWs)

See [03-design.md](03-design.md) for detailed design.
