---
name: weekly-review
description: Weekly development review - progress summary, stale items, upcoming priorities
argument-hint: (no arguments needed)
---

# Weekly Development Review

Generate a comprehensive weekly review of Typhon development progress, identify stale items, and suggest priorities.

## Help

If `$ARGUMENTS` contains `--help` or `-h`, display the following and **stop** — do not execute the workflow.

```
/weekly-review

  Weekly development review — progress summary, stale items, upcoming priorities.

Arguments:
  --help, -h      Show this help

What it does:
  1. Gathers recent activity (closed issues, new issues, commits)
  2. Shows current work status by category
  3. Identifies stale items needing attention
  4. Reports phase progress and documentation health
  5. Suggests priorities for next week

Examples:
  /weekly-review
```

## Purpose

This skill provides a "pulse check" on the project:
- What got done this week
- What's currently in flight
- What's getting stale
- What should be prioritized next

## Workflow

### 1. Gather Recent Activity

**Closed issues (last 7 days):**

Use `mcp__GitHub__list_issues` with:
- owner: `"nockawa"`
- repo: `"Typhon"`
- state: `"closed"`
- per_page: `50`

Filter the returned results to those closed within the last 7 days based on the `closed_at` field.

**Open issues (recently created):**

Use `mcp__GitHub__list_issues` with:
- owner: `"nockawa"`
- repo: `"Typhon"`
- state: `"open"`
- per_page: `50`

Filter to those created within the last 7 days based on the `created_at` field.

**Recent commits:**

Use `mcp__GitHub__list_commits` with:
- owner: `"nockawa"`
- repo: `"Typhon"`

Returns an array of commit objects with sha, message, date, author.

### 2. Check Current Work Status

**Always pipe `gh project item-list` directly to Python** (see `.claude/skills/_helpers.md` Section 2):

```bash
# All project items with their status -- pipe directly to Python
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
    print(f'#{n} | {s} | {p} | {a} | {e} | {t}')
"
```

Parse the output to filter and categorize items.

Categorize by status:
- In Progress
- Review
- Ready
- Research
- Backlog

### 3. Identify Stale Items

Flag items that need attention:
- **In Progress > 7 days**: May be stuck or forgotten
- **Review > 3 days**: Waiting too long
- **Ready > 14 days**: Should be started or deprioritized
- **No update > 30 days**: Consider closing or archiving

### 4. Check Phase Progress

For each active Phase:
- Count completed sub-issues
- Count remaining sub-issues
- Calculate percentage complete

### 5. Review Documentation Health

Check for:
- Design docs without linked issues (orphaned)
- Issues without design docs (missing design)
- Outdated docs in `claude/design/` that should move to `reference/`

```bash
# List design docs
ls claude/design/

# Check if corresponding issues exist
```

### 6. Generate Report

```
Typhon Weekly Review
======================================================

Week of [DATE]

## Completed This Week

| # | Title | Area |
|---|-------|------|
| #42 | Add telemetry to PagedMMF | Storage |
| #43 | Fix revision chain bug | MVCC |

## Currently In Progress

| # | Title | Days | Priority |
|---|-------|------|----------|
| #47 | Transaction telemetry | 3 | P1 |

## Needs Attention

### Stale Items
- #38 "Query optimizer" - In Progress for 12 days (!)
- #35 "Index rebuild" - Ready for 21 days

### Blocked
- #44 waiting on #43 (now resolved)

## Phase Progress

### Telemetry & Observability
40% (4/10 issues)

### Query Engine
0% (0/5 issues) - not started

## Suggested Priorities for Next Week

Based on priority and phase goals:
1. #48 Transaction telemetry (P1, Telemetry phase)
2. #49 Query parser basics (P1, Query phase)
3. #50 Error handling improvements (P2, Reliability)

## Documentation Status

- Design docs: 5 active, 2 could move to reference/
- Overview docs: Last updated 14 days ago
- ADRs: 30 total, none added this week

======================================================
```

### 7. Offer Actions

After presenting the report, ask:
```
Question: "What would you like to do?"
Header: "Actions"
Options:
  - Start work on top priority (/start-task)
  - Close stale items
  - Update documentation
  - Nothing for now
```

## Notes

- Run this skill at the start of each week (Monday recommended)
- Consider creating a calendar reminder
- The report is designed to be copy-pasteable for standups or notes
