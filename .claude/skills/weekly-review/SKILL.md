---
name: weekly-review
description: Weekly development review - progress summary, stale items, upcoming priorities
argument-hint: (no arguments needed)
---

# Weekly Development Review

Generate a comprehensive weekly review of Typhon development progress, identify stale items, and suggest priorities.

## Purpose

This skill provides a "pulse check" on the project:
- What got done this week
- What's currently in flight
- What's getting stale
- What should be prioritized next

## Workflow

### 1. Gather Recent Activity

```bash
# Issues closed in last 7 days
gh issue list --repo nockawa/Typhon --state closed --json number,title,closedAt,labels --limit 50

# Issues opened in last 7 days
gh issue list --repo nockawa/Typhon --state open --json number,title,createdAt,labels --limit 50

# Recent commits
gh api repos/nockawa/Typhon/commits --jq '.[0:20] | .[] | {sha: .sha[0:7], message: .commit.message, date: .commit.author.date}'
```

### 2. Check Current Work Status

```bash
# All project items with their status
gh project item-list 7 --owner nockawa --format json
```

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
📊 Typhon Weekly Review
══════════════════════════════════════════════════════

📅 Week of [DATE]

## 🎉 Completed This Week

| # | Title | Area |
|---|-------|------|
| #42 | Add telemetry to PagedMMF | Storage |
| #43 | Fix revision chain bug | MVCC |

## 🚧 Currently In Progress

| # | Title | Days | Priority |
|---|-------|------|----------|
| #47 | Transaction telemetry | 3 | P1 |

## ⚠️ Needs Attention

### Stale Items
- #38 "Query optimizer" - In Progress for 12 days (!)
- #35 "Index rebuild" - Ready for 21 days

### Blocked
- #44 waiting on #43 (now resolved)

## 📈 Phase Progress

### Telemetry & Observability
████████░░░░░░░░░░░░ 40% (4/10 issues)

### Query Engine
░░░░░░░░░░░░░░░░░░░░ 0% (0/5 issues) - not started

## 🎯 Suggested Priorities for Next Week

Based on priority and phase goals:
1. #48 Transaction telemetry (P1, Telemetry phase)
2. #49 Query parser basics (P1, Query phase)
3. #50 Error handling improvements (P2, Reliability)

## 📚 Documentation Status

- Design docs: 5 active, 2 could move to reference/
- Overview docs: Last updated 14 days ago
- ADRs: 30 total, none added this week

══════════════════════════════════════════════════════
```

### 7. Offer Actions

After presenting the report, ask:
```
Question: "What would you like to do?"
Header: "Actions"
Options:
  - Start work on top priority (/start-work)
  - Close stale items
  - Update documentation
  - Nothing for now
```

## Notes

- Run this skill at the start of each week (Monday recommended)
- Consider creating a calendar reminder
- The report is designed to be copy-pasteable for standups or notes
