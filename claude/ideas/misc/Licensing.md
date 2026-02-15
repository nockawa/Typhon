# Licensing Strategy for Typhon

**Date:** 2025-12-26 (original conversation), 2026-02-15 (decisions made)
**Status:** Decided — LICENSE.md committed
**Captures:** Licensing model exploration and final decisions

## Decision Summary

Typhon uses a **source-available** license combining two PolyForm concepts:

| Component | Based On | Purpose |
|-----------|----------|---------|
| **Size gate** | PolyForm Small Business | Free for small organizations, paid for large ones |
| **Competition restriction** | PolyForm Shield | Prevents competing products/services regardless of size |
| **Pre-1.0 exemption** | Custom clause | All pre-release versions free for any use |

### Thresholds

- **Revenue:** $2,000,000 USD annual
- **Headcount:** 50 individuals (employees + contractors)
- Organizations exceeding **either** threshold must obtain a commercial license (for versions 1.0.0+)

### Commercial Contact

licensing@log2n.io

## Rationale

### Why Source-Available (Not OSS)

- **Direct revenue path** — the license creates paying customers, no need for separate monetization layers
- **Free-rider protection** — large companies cannot use Typhon without contributing financially
- **No bait-and-switch risk** — starting source-available from day one means no future license change backlash
- **Contributor impact is minimal** — database engines get very few external contributors regardless of license (SQLite, DuckDB precedent)
- **Fork protection** — competitors cannot fork and compete, cloud providers cannot offer managed service

### Why Pre-1.0 Exemption

- Eliminates any perception of overvaluing unfinished software
- Acts as a natural beta program — early adopters get free usage as reward for risk
- Clean transition: everyone knows from day one that 1.0 = license enforcement
- No license change needed at 1.0 — the terms were always there

### Why $2M / 50 Employees

- **$2M** catches mid-size indie studios and above — solo developers and small teams are safely free
- **50 employees** is tighter than the PolyForm default (100) — catches AA studios while keeping small teams free
- Thresholds can be adjusted in future versions without affecting existing users (each version keeps its own terms)

### Why Shield (Competition Restriction)

- Prevents a cloud provider from offering "Managed Typhon" at any organization size
- Prevents competitors from building a competing database product on Typhon's code
- Applies regardless of organization size — even a small company can't build a competing service
- Low cost (no one is building Typhon-as-a-Service today) but provides peace of mind

## Key Design Decisions

| Decision | Choice | Alternative Considered |
|----------|--------|----------------------|
| License family | PolyForm-based custom | BSL (time-based conversion), SSPL, MIT |
| Starting position | Source-available from day one | Start OSS, switch later |
| Revenue threshold | $2M | $5M, $10M |
| Headcount threshold | 50 | 100 (PolyForm default), 200 |
| Competition clause | Yes (Shield) | Size-gate only (Small Business) |
| Pre-1.0 exemption | Yes | No (enforce from first release) |
| Threshold evolution | Per-version (old versions keep old terms) | Global retroactive changes |

## Future Considerations

- **Threshold adjustments** — can tighten or loosen in future versions; existing versions keep their terms
- **CLA for contributors** — may be needed if/when external contributions arrive; keeps option to adjust license terms
- **Commercial pricing structure** — currently "contact us"; formalize tiers when paying customers exist
- **Formal legal review** — the LICENSE.md is a clear-language document based on PolyForm concepts; consider professional legal review before 1.0

## Research Background

### PolyForm License Family

| License | Who Can Use Freely | Who Must Pay |
|---------|-------------------|--------------|
| **Polyform Noncommercial** | Non-profits, education, personal use | Any commercial use |
| **Polyform Perimeter** | Everyone except those offering it as a competing product | Companies offering it as a competing service |
| **Polyform Shield** | Everyone except competitors of the provider | Companies competing with you (broader than Perimeter) |
| **Polyform Small Business** | Organizations under revenue/employee threshold | Larger organizations regardless of use |
| **Polyform Free Trial** | Evaluation/trial use only | Production use |

### Database Licensing Landscape

| Database | License | Model |
|----------|---------|-------|
| CockroachDB | BSL | Converts to Apache 2.0 after 3 years |
| MariaDB | BSL | Created BSL originally |
| MongoDB | SSPL | Server Side Public License |
| Redis | RSALv2 + SSPLv1 | Dual restrictive |
| Elasticsearch | Elastic License 2.0 + SSPL | Dual restrictive |
| YugabyteDB | Apache 2.0 + Polyform Free Trial | Core OSS, platform restricted |
| EPPlus (.NET) | PolyForm Noncommercial + commercial | Similar model to Typhon |
| **Typhon** | **PolyForm Small Business + Shield + pre-1.0 exemption** | **Size gate + competition protection** |

## Sources

- Original conversation: [Polyform license model in databases](https://claude.ai/share/06df499c-d24e-49e6-a4ed-a092205e73db)
- Analysis session: Claude Code, 2026-02-15 (OSS vs source-available trade-offs, contributor/fork analysis, threshold selection)
