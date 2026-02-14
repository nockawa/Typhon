# Licensing Strategy for Typhon

**Date:** 2025-12-26 (original conversation), 2026-02-14 (captured)
**Status:** Needs decision
**Captures:** Conversation exploring licensing models for commercial protection while enabling adoption

## Overview

Typhon currently has **no LICENSE file** — which defaults to "all rights reserved" under copyright law. Before any public release, a deliberate licensing decision is needed that balances community adoption against commercial protection (particularly the "AWS problem" — cloud providers offering your database as a managed service).

## Key Findings

### Polyform Licenses

Polyform is a family of source-available licenses with distinct variants:

| License | Who Can Use Freely | Who Must Pay |
|---------|-------------------|--------------|
| **Polyform Noncommercial** | Non-profits, education, personal use | Any commercial use |
| **Polyform Perimeter** | Everyone except those offering it as a competing product | Companies offering it as a competing service |
| **Polyform Shield** | Everyone except competitors of the provider | Companies competing with you (broader than Perimeter) |
| **Polyform Small Business** | Organizations under revenue/employee threshold | Larger organizations regardless of use |
| **Polyform Free Trial** | Evaluation/trial use only | Production use |

### Polyform Small Business — Best Fit for "I Want Them to Pay"

The conversation converged on **PolyForm Small Business** as the best match for Typhon's goals. Standard thresholds:
- Less than **$2M annual revenue**, or
- Fewer than **100 employees**

These thresholds are adjustable in the commercial terms.

### Business Model with PolyForm Small Business

1. Release under PolyForm Small Business
2. Small startups, hobbyists, and small shops use it **free** (builds adoption)
3. Enterprises contact you for a **commercial license**
4. Pricing negotiated based on size, usage, and support needs

### Real-World Precedent

**EPPlus** (popular .NET Excel library) uses a similar model: PolyForm Noncommercial for the open version + commercial license for business use.

### Alternative: BSL (Business Source License)

BSL is more established in the database world. Key difference from Polyform:

| Aspect | Polyform (Perimeter/Shield) | BSL |
|--------|---------------------------|-----|
| Anti-competition | Yes | Yes |
| Converts to open source | No (stays source-available) | Yes (typically after 3-4 years) |
| Customization | Pick from preset variants | Parameterized (you define restrictions) |
| Community perception | Less known | More established in database world |
| Used by | YugabyteDB (Polyform Free Trial) | CockroachDB, MariaDB, Couchbase |

Other source-available licenses used by databases:
- **MongoDB** — SSPL (Server Side Public License)
- **Redis** — RSALv2 + SSPLv1
- **Elasticsearch** — Elastic License 2.0 + SSPL

## Considerations

- **No LICENSE file currently** — legally ambiguous; blocks any community contribution or adoption
- **Dual licensing is common** — e.g., YugabyteDB uses Apache 2.0 for the core database + Polyform Free Trial for the managed platform layer
- **Enterprises need a clear commercial path** — some won't evaluate software without visible pricing/licensing
- **The license is self-enforcing** — but you won't know who's using it; consider telemetry or registration for commercial users
- **Community perception matters** — permissive licenses (MIT, Apache 2.0) build trust fastest, but make monetization harder

## Open Questions

1. **Which license family?** Polyform Small Business (revenue-based gate) vs BSL (time-based conversion to open source) vs dual licensing (free core + commercial enterprise tier)?
2. **What thresholds?** $2M revenue / 100 employees is the Polyform default — is that right for Typhon's target market (game studios)?
3. **Dual licensing split?** Should the core engine be more permissive (Apache 2.0) with enterprise features (replication, advanced telemetry, support) under commercial license?
4. **When to decide?** Before first public release is mandatory, but earlier decisions shape architecture (e.g., what goes in "core" vs "enterprise")

## Source

Original conversation: [Polyform license model in databases](https://claude.ai/share/06df499c-d24e-49e6-a4ed-a092205e73db)
