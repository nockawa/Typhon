# UoW Crash Recovery Design

> How to ensure Unit of Work atomicity survives server crashes.

---

## Status: Design Complete

**Recommended Approach**: Hybrid - UoW Registry + Epoch Identification

---

## The Core Problem

When a Unit of Work contains multiple committed transactions and the server crashes before `Flush()`, those transactions should be rolled back - but currently they appear committed because `IsolationFlag = false`.

```
UoW created
├── Tx1.Commit()  → IsolationFlag=false, VISIBLE
├── Tx2.Commit()  → IsolationFlag=false, VISIBLE
├── [CRASH]
└── Flush() never called

Current behavior: Tx1, Tx2 appear committed (WRONG)
Required behavior: Tx1, Tx2 rolled back (database unchanged)
```

---

## Key Insight

The solution requires two things:
1. **Identify** which revisions belong to which UoW (epoch in revision)
2. **Know** which UoWs are incomplete (registry with pending/committed status)

Neither alone is sufficient:
- Epoch alone requires scanning entire DB on recovery
- Registry alone can't identify revisions without epoch stamps

---

## Solution Summary

| Component | Change |
|-----------|--------|
| `CompRevStorageElement` | +2 bytes for `UowEpoch` field (12 bytes total) |
| New segment | UoW Registry tracking pending/committed epochs |
| Visibility check | 3 comparisons: IsolationFlag, same-epoch, bitmap |
| Recovery | O(1) - scan registry, void pending epochs |

---

## Documents

| File | Content |
|------|---------|
| [01-problem.md](01-problem.md) | The crash recovery problem explained |
| [02-approaches.md](02-approaches.md) | 5 approaches analyzed with pros/cons |
| [03-design.md](03-design.md) | Detailed design for the hybrid approach |
| [04-implementation.md](04-implementation.md) | Files to modify, phased plan |

---

## Design Decisions

| Decision | Choice | Rationale |
|----------|--------|-----------|
| Epoch size | `ushort` (2 bytes) | 65536 values, recycled via GC |
| Registry location | Dedicated segment | Cleaner separation, easier to extend |
| Concurrent UoW limit | 4096+ | Hierarchical bitmap in registry |

---

## Related

- [02-execution.md](../../overview/02-execution.md) - Unit of Work design
- [async-uow/](../async-uow/) - Async UoW integration
