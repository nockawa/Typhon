---
name: mountain-view
description: Full backlog analysis - see the entire mountain of work ahead
argument-hint: (no arguments needed)
---

# Mountain View - Full Backlog Analysis

Get a comprehensive "helicopter view" of all work in the Typhon project - past, present, and future. Understand the full scope of what's been done and what remains.

## Purpose

This skill answers the question: **"How big is the mountain to climb?"**

It provides:
- Total work inventory
- Progress by phase and area
- Effort distribution
- Velocity insights
- Time-to-completion estimates

## Workflow

### 1. Gather All Issues

```bash
# All open issues
gh issue list --repo nockawa/Typhon --state open --json number,title,labels,createdAt --limit 200

# All closed issues (for velocity calculation)
gh issue list --repo nockawa/Typhon --state closed --json number,title,labels,closedAt --limit 200

# All project items with full field data — pipe directly to Python (see _helpers.md)
gh project item-list 7 --owner nockawa --limit 200 --format json 2>&1 | python3 -c "
import json, sys
items = json.load(sys.stdin)['items']
for item in items:
    s = item.get('status', '?')
    n = item.get('content', {}).get('number', '?')
    t = item.get('title', 'untitled')
    p = item.get('priority', '?')
    a = item.get('area', '?')
    e = item.get('estimate', '?')
    ph = item.get('phase', '?')
    print(f'#{n} | {s} | {p} | {a} | {e} | {ph} | {t}')
"
```

### 2. Calculate Totals

**By Status:**
- Backlog: X issues
- Research: X issues
- Ready: X issues
- In Progress: X issues
- Review: X issues
- Done: X issues

**By Priority:**
- P0-Critical: X issues
- P1-High: X issues
- P2-Medium: X issues
- P3-Low: X issues

**By Area:**
- Database: X issues
- Storage: X issues
- MVCC: X issues
- etc.

**By Phase:**
- Telemetry: X/Y complete
- Query: X/Y complete
- WAL: X/Y complete
- Reliability: X/Y complete
- Infrastructure: X/Y complete

### 3. Calculate Effort

Using Estimate field:
- XS = 0.5 days
- S = 0.5 days
- M = 1.5 days
- L = 4 days
- XL = 8 days

Sum total estimated effort remaining.

### 4. Calculate Velocity

Look at last 30/60/90 days:
- Issues completed per week
- Average estimate vs actual (if trackable)
- Trend (accelerating/decelerating/stable)

### 5. Project Completion

Based on velocity and remaining effort:
- Optimistic estimate (current velocity maintained)
- Pessimistic estimate (50% slower)
- Per-phase estimates

### 6. Generate Report

```
🏔️ Typhon Mountain View
══════════════════════════════════════════════════════

📊 OVERALL STATISTICS
────────────────────────────────────────────────────────

Total Issues: 85
  ├── Open: 47
  │   ├── Backlog: 22
  │   ├── Research: 5
  │   ├── Ready: 12
  │   ├── In Progress: 6
  │   └── Review: 2
  └── Closed: 38

Completion Rate: 45%
████████████████████░░░░░░░░░░░░░░░░░░░░░░░░ 45%

📈 VELOCITY (Last 30 Days)
────────────────────────────────────────────────────────

Issues Closed: 12
Average: 3 issues/week
Trend: ↑ Accelerating (+20% vs previous month)

📦 BY PHASE
────────────────────────────────────────────────────────

Telemetry & Observability
█████████████████░░░░░░░░░░░░░░░░░░░░░░░░░░ 40%
└── 8/20 issues complete | ~24 days remaining

Query Engine
███░░░░░░░░░░░░░░░░░░░░░░░░░░░░░░░░░░░░░░░░ 10%
└── 2/18 issues complete | ~48 days remaining

Write-Ahead Logging (WAL)
░░░░░░░░░░░░░░░░░░░░░░░░░░░░░░░░░░░░░░░░░░░ 0%
└── 0/12 issues complete | ~36 days remaining

Reliability
██████░░░░░░░░░░░░░░░░░░░░░░░░░░░░░░░░░░░░░ 15%
└── 3/20 issues complete | ~51 days remaining

Infrastructure
██████████████████████████░░░░░░░░░░░░░░░░░ 60%
└── 6/10 issues complete | ~12 days remaining

🎯 BY PRIORITY
────────────────────────────────────────────────────────

P0-Critical:  2 open  ⚠️ (should be 0!)
P1-High:     15 open
P2-Medium:   22 open
P3-Low:       8 open

🗂️ BY AREA
────────────────────────────────────────────────────────

Storage:      12 issues (25%)  ████████████░░░░░░░░░░░░░░░░░░░░░░░░░░░░
Transactions:  8 issues (17%)  ████████░░░░░░░░░░░░░░░░░░░░░░░░░░░░░░░░
MVCC:          7 issues (15%)  ███████░░░░░░░░░░░░░░░░░░░░░░░░░░░░░░░░░
Indexes:       6 issues (13%)  ██████░░░░░░░░░░░░░░░░░░░░░░░░░░░░░░░░░░
Database:      5 issues (10%)  █████░░░░░░░░░░░░░░░░░░░░░░░░░░░░░░░░░░░
Concurrency:   4 issues (8%)   ████░░░░░░░░░░░░░░░░░░░░░░░░░░░░░░░░░░░░
Memory:        3 issues (6%)   ███░░░░░░░░░░░░░░░░░░░░░░░░░░░░░░░░░░░░░
Schema:        2 issues (4%)   ██░░░░░░░░░░░░░░░░░░░░░░░░░░░░░░░░░░░░░░

⏱️ EFFORT ESTIMATE
────────────────────────────────────────────────────────

Total Remaining Effort: ~120 days
  ├── XS items: 8 (4 days)
  ├── S items: 15 (7.5 days)
  ├── M items: 12 (18 days)
  ├── L items: 8 (32 days)
  ├── XL items: 4 (32 days)
  └── Unestimated: 10 (~25 days assumed)

📅 PROJECTED COMPLETION
────────────────────────────────────────────────────────

At current velocity (3 issues/week):
├── Optimistic: ~16 weeks (April 2026)
├── Realistic:  ~20 weeks (May 2026)
└── Pessimistic: ~28 weeks (August 2026)

🚨 ATTENTION ITEMS
────────────────────────────────────────────────────────

Critical (P0) Issues:
  • #23 "Memory leak in page cache" - 5 days old
  • #31 "Transaction deadlock" - 2 days old

Oldest Open Issues (potential stale):
  • #12 "String table defragmentation" - 45 days
  • #8 "Query optimizer prototype" - 38 days

Large Unestimated:
  • #40 "WAL implementation" - likely XL
  • #41 "Checkpoint mechanism" - likely L

══════════════════════════════════════════════════════

💡 The mountain is tall but climbable. Focus on P0/P1 items
   and complete the Telemetry phase before starting Query.
```

### 7. Offer Actions

After presenting the report, ask:
```
Question: "What would you like to explore?"
Header: "Drill Down"
Options:
  - Focus on a specific phase
  - Focus on a specific area
  - View all P0/P1 items
  - Export to markdown file
  - Nothing, just wanted the overview
```

## Usage Notes

- Run this skill periodically (monthly recommended) or when planning
- Use before major planning sessions
- Helps communicate project scope to stakeholders
- Export option creates a shareable snapshot

## Calculation Details

**Velocity calculation:**
- Uses issues closed per week over rolling window
- Weighted average (recent weeks count more)

**Effort conversion:**
- Based on Estimate field values
- Unestimated items assumed as M (1.5 days)

**Completion projection:**
- Remaining effort / weekly velocity
- Pessimistic adds 40% buffer
- Assumes sustained effort (adjust for vacations, etc.)
