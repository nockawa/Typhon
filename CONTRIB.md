# Contributing to Typhon

**Date:** 2026-01-25
**Status:** Implemented
**Author:** Claude Code + nockawa

---

## Summary

This document proposes a unified development workflow for Typhon that integrates:

- **Planning & Tracking**: GitHub Projects as the single source of truth for work status
- **Knowledge Base**: The `claude/` documentation ecosystem as living architecture memory
- **Development Loop**: An explicit lifecycle from ideation → shipping
- **Automation**: Claude Code as an active assistant enforcing consistency and reducing friction
- **Tooling**: Rider + CLI + GitHub as the developer backbone

The goal is to create a system where:
1. Nothing falls through the cracks
2. Context is always recoverable (even after months away)
3. The "next thing to work on" is always clear
4. Documentation stays synchronized with reality

---

## Table of Contents

1. [The Big Picture](#the-big-picture)
2. [Lifecycle Stages](#lifecycle-stages)
3. [Artifact Relationships](#artifact-relationships)
4. [Workflow Rituals](#workflow-rituals)
5. [Claude Code Integration Points](#claude-code-integration-points)
6. [Automation Specifications](#automation-specifications)
7. [Views & Dashboards](#views--dashboards)
8. [Developer's Daily Guide](#developers-daily-guide)
9. [Implementation Plan](#implementation-plan)

---

## The Big Picture

```
┌─────────────────────────────────────────────────────────────────────────────┐
│                           TYPHON DEVELOPMENT SYSTEM                         │
├─────────────────────────────────────────────────────────────────────────────┤
│                                                                             │
│  ┌─────────────┐     ┌─────────────┐     ┌─────────────┐     ┌───────────┐  │
│  │   IDEAS     │────►│  RESEARCH   │────►│   DESIGN    │────►│   CODE    │  │
│  │  (claude/)  │     │  (claude/)  │     │  (claude/)  │     │  (src/)   │  │
│  └─────────────┘     └─────────────┘     └─────────────┘     └───────────┘  │
│        │                   │                   │                   │        │
│        ▼                   ▼                   ▼                   ▼        │
│  ┌─────────────────────────────────────────────────────────────────────┐    │
│  │                    GITHUB PROJECT (Source of Truth)                 │    │
│  │  ┌─────────┐  ┌──────────┐  ┌────────────┐  ┌─────────┐  ┌───────┐  │    │
│  │  │ Backlog │─►│ Research │─►│ Ready/Todo │─►│In Prog. │─►│ Done  │  │    │
│  │  └─────────┘  └──────────┘  └────────────┘  └─────────┘  └───────┘  │    │
│  └─────────────────────────────────────────────────────────────────────┘    │
│        │                                                       │            │
│        ▼                                                       ▼            │
│  ┌─────────────┐                                       ┌─────────────────┐  │
│  │  ROADMAP    │◄──────────────────────────────────────│    REFERENCE    │  │
│  │  (claude/)  │      Completed items feed back        │    (claude/)    │  │
│  └─────────────┘                                       └─────────────────┘  │
│                                                                             │
│  ┌──────────────────────────────────────────────────────────────────────┐   │
│  │  CLAUDE CODE: Watches, prompts, automates, correlates, enforces      │   │
│  └──────────────────────────────────────────────────────────────────────┘   │
│                                                                             │
└─────────────────────────────────────────────────────────────────────────────┘
```

### Core Principles

1. **GitHub Project = Source of Truth** — Where is this work right now? What's the roadmap?
2. **claude/ docs = Knowledge** — What do we know about this problem/solution?
3. **Issues = Discussion** — Comments, decisions, blockers for specific work items
4. **Claude Code = Glue** — Keeps everything synchronized and prompts for missing pieces

> **Note:** ROADMAP.md has been retired. Work planning and tracking now live entirely in the [GitHub Project](https://github.com/users/nockawa/projects/7) with views for Workflow Board, Roadmap, By Area, By Priority, and By Phase.

---

## Lifecycle Stages

### Stage 1: Ideation

**Trigger:** "I wonder if we should..." / "What about..." / Random thought

| Artifact | Location | Required Fields |
|----------|----------|-----------------|
| Idea doc | `claude/ideas/[category]/Name.md` | Date, Status, The Idea, Why It Matters |
| GitHub Issue | Optional (if worth tracking) | Label: `idea`, Status: `Backlog` |

**Claude's Role:**
- Prompt: "Should I capture this as an idea doc?"
- Create with template
- Ask: "Worth adding to GitHub for tracking, or just parking it?"

**Example Claude Code Prompts:**
```
"I have an idea for spatial indexing support, capture it"
"Create an idea doc about query caching in the database-engine category"
"/create-issue Add support for spatial indexing"  ← if worth tracking immediately
```

### Stage 2: Research

**Trigger:** Idea needs exploration / Multiple approaches exist / Unknown territory

| Artifact | Location | Required Fields |
|----------|----------|-----------------|
| Research doc | `claude/research/[category]/Name.md` | Context, Questions, Options, Recommendation |
| GitHub Issue | Required | Label: `research`, Status: `Research` |
| Linked ADRs | `claude/adr/` | Created when key decisions are made |

**Claude's Role:**
- Prompt: "This needs research before we can design. Create research doc?"
- Link issue ↔ research doc bidirectionally
- When research concludes: "Ready to promote to design?"

**Example Claude Code Prompts:**
```
"Research the best approach for write-ahead logging in our persistence layer"
"Create a research doc comparing B+Tree vs LSM-Tree for our index layer"
"/create-issue Research WAL implementation strategies"  ← with label: research
"Promote the WAL research to a design doc, the approach is decided"
```

### Stage 3: Design

**Trigger:** Research complete / Approach chosen / Ready to plan implementation

| Artifact | Location | Required Fields |
|----------|----------|-----------------|
| Design doc | `claude/design/[category]/Name.md` | Summary, Goals, Non-Goals, Design, Testing Strategy |
| GitHub Issue | Required | Label: `enhancement`/`bug`, Status: `Ready` |
| Branch | `feature/xxx` or `fix/xxx` | Created when work begins |

**Claude's Role:**
- Generate design doc from research conclusions
- Validate design covers: data structures, API, edge cases, tests
- Prompt: "Design looks complete. Create branch and start implementation?"

**Example Claude Code Prompts:**
```
"Create a design doc for the WAL system based on the research conclusions"
"Review the design for QueryEngine — does it cover edge cases and testing?"
"/create-issue Implement WAL system"  ← with design doc linked
"/start-work 42"  ← when ready to begin implementation
```

### Stage 4: Implementation

**Trigger:** Design approved / Ready to code

| Artifact | Location | Status |
|----------|----------|--------|
| GitHub Issue | — | Status: `In Progress` |
| Branch | Active | Commits reference issue |
| Design doc | Updated | Status: `In progress`, Branch noted |

**Claude's Role:**
- Track progress against design doc checklist
- Remind about missing tests
- Prompt when stuck: "Blocked? Should we update the issue?"

**Example Claude Code Prompts:**
```
"/start-work 42"  ← updates status, creates branch, checks design doc
"/dev-status"  ← see what's currently in progress
"What's left to do for #42 according to the design doc?"
"I'm blocked on #42, update the issue with a note about the concurrency problem"
```

### Stage 5: Completion

**Trigger:** PR merged / Feature complete

| Artifact | Action |
|----------|--------|
| GitHub Issue | Closed (auto or manual) |
| Design doc | → `reference/` (if useful) or `archive/` |
| ROADMAP.md | Updated with completion date |
| overview/ | Updated if architectural impact |
| ADR | Created if significant decision was made |

**Claude's Role:**
- Prompt: "Feature complete! Let me update the roadmap and archive the design doc."
- Check: "Should we update any overview/ docs?"
- Check: "Any architectural decisions worth an ADR?"

**Example Claude Code Prompts:**
```
"/complete-work 42"  ← closes issue, archives design, updates docs
"/weekly-review"  ← see what was completed this week
"/mountain-view"  ← how much work remains overall
"Create an ADR for the decision to use circular buffers for revision chains"
```

---

## Artifact Relationships

```
                    ┌─────────────────┐
                    │   ROADMAP.md    │
                    │  (Strategic)    │
                    └────────┬────────┘
                             │ references
                             ▼
┌──────────────┐    ┌─────────────────┐    ┌──────────────┐
│  ideas/      │───►│  GitHub Issue   │◄───│  design/     │
│              │    │  (Work Item)    │    │              │
└──────────────┘    └────────┬────────┘    └──────────────┘
                             │                     ▲
                             │ linked to           │ based on
                             ▼                     │
                    ┌─────────────────┐    ┌──────────────┐
                    │    Branch       │    │  research/   │
                    │    (Code)       │    │              │
                    └────────┬────────┘    └──────────────┘
                             │
                             │ produces
                             ▼
                    ┌─────────────────┐    ┌──────────────┐
                    │      PR         │───►│  reference/  │
                    │                 │    │              │
                    └─────────────────┘    └──────────────┘
                                                  │
                                           updates│
                                                  ▼
                                          ┌──────────────┐
                                          │  overview/   │
                                          │  (Living)    │
                                          └──────────────┘
```

### Linking Conventions

**In GitHub Issues:**
```markdown
## Related Documents
- Design: [QueryEngine](../claude/design/QueryEngine.md)
- Research: [QuerySystem](../claude/research/QuerySystem.md)
- ADR: [ADR-025](../claude/adr/025-query-execution-model.md)
```

**In Design Docs:**
```markdown
**GitHub Issue:** #42
**Branch:** `feature/query-engine`
```

**In ROADMAP.md:**
```markdown
| Query System | 🚧 | [#42](https://github.com/nockawa/Typhon/issues/42) | [Design](design/QueryEngine.md) |
```

---

## Workflow Rituals

### Daily Start (5 min)

1. **Check GitHub Project board** — What's In Progress?
2. **Ask Claude:** "What am I working on?" (Claude reads Project + ROADMAP)
3. **Review any blockers** — Update issue status if needed

### Before Starting New Work

1. **Check if design exists** — Is there a `design/` doc?
2. **Check if research is complete** — Any open questions?
3. **Update issue status** → `In Progress`
4. **Create branch** with proper naming

**Claude Prompt:** "Starting work on #42"
- Claude verifies design exists
- Claude updates issue status
- Claude reminds of branch naming convention

### After Completing Work

1. **Update issue** with summary of what was done
2. **Ask Claude:** "Feature complete for #42"
   - Claude moves design doc → reference/archive
   - Claude updates ROADMAP.md
   - Claude checks for overview/ updates

### Weekly Review (30 min)

1. **Review ROADMAP.md** — Still accurate?
2. **Check `ideas/`** — Anything to promote or archive?
3. **Check `research/`** — Any stale research?
4. **Review GitHub Project** — Anything stuck?
5. **Ask Claude:** "Give me a status summary"

---

## Claude Code Integration Points

### Skill: `/dev-status` — Where Are We?

```markdown
---
name: status
description: Show current development status across all systems
---

Reads and correlates:
- GitHub Project (In Progress items)
- ROADMAP.md (Current Phase, Active Work)
- Recent git activity

Output:
- Current focus area
- Active work items with links
- Any stale items (no activity > 7 days)
- Suggested next actions
```

### Skill: `/create-issue` — Create a New Work Item

```markdown
---
name: create-issue
description: Create a GitHub issue and add it to the Typhon dev project
argument-hint: [title] or leave empty for interactive mode
---

Actions:
1. Gather info interactively (title, description, type labels, area, priority, phase, estimate)
2. Create GitHub issue assigned to nockawa
3. Add issue to "Typhon dev" project (#7)
4. Set all project fields (Status, Priority, Phase, Area, Estimate)
5. Report summary with issue link and field values
```

### Skill: `/start-work` — Begin a Work Item

```markdown
---
name: start-work
description: Start working on a GitHub issue
argument-hint: [issue number or title]
---

Actions:
1. If no argument: list Ready/Backlog issues to pick from, or offer to create a new one
2. If non-numeric argument: offer to create issue or search existing ones
3. Verify design doc exists (prompt to create if missing)
4. Update issue status → In Progress
5. Create branch if needed (feature/XX-name or fix/XX-name)
6. Update design doc with branch name
7. Report readiness
```

### Skill: `/complete-work` — Finish a Work Item

```markdown
---
name: complete-work
description: Mark work complete and update all artifacts
argument-hint: [issue number]
---

Actions:
1. Update issue status → Done (or close)
2. Move design doc → reference/ or archive/
3. Update ROADMAP.md completed section
4. Check for overview/ updates needed
5. Prompt for ADR if significant decision
6. Report summary
```

### Skill: `/weekly-review` — Generate Weekly Summary

```markdown
---
name: weekly-review
description: Generate weekly development summary
---

Analyzes:
- Git commits this week
- Issues closed/opened
- Docs created/modified
- Project board changes

Output:
- Progress summary
- Stale items warning
- Suggested cleanups
- Roadmap alignment check
```

### Skill: `/mountain-view` — How Big Is the Mountain?

```markdown
---
name: mountain-view
description: Show the full scope of remaining work
---

Aggregates:
- Backlog items with estimates
- In-progress work
- Research items pending
- Ideas worth exploring

Output:
- Total estimated effort remaining
- Breakdown by Area
- Breakdown by Priority
- Risk areas (large items, stale items)
```

### Hook: Post-Commit Analysis

```yaml
# .claude/hooks/post-commit.yaml
trigger: after git commit
actions:
  - Check if commit references an issue
  - Warn if large change without design doc
  - Suggest updating relevant docs
```

### Hook: Session Start

```yaml
# .claude/hooks/session-start.yaml
trigger: claude code session start
actions:
  - Show current phase from ROADMAP.md
  - List In Progress items
  - Note any stale work (>7 days no activity)
```

---

## Automation Specifications

### GitHub Actions: Project Sync

```yaml
# .github/workflows/project-sync.yml
name: Sync Project Status

on:
  issues:
    types: [opened, closed, labeled]
  pull_request:
    types: [opened, closed, merged]

jobs:
  sync:
    runs-on: ubuntu-latest
    steps:
      - name: Add new issues to project
        if: github.event_name == 'issues' && github.event.action == 'opened'
        run: gh project item-add 7 --owner nockawa --url ${{ github.event.issue.html_url }}

      - name: Move to Done on close
        if: github.event_name == 'issues' && github.event.action == 'closed'
        run: |
          # Update project item status to Done

      - name: Move to In Progress on PR
        if: github.event_name == 'pull_request' && github.event.action == 'opened'
        run: |
          # Find linked issue, update status
```

### GitHub Actions: Doc Link Validator

```yaml
# .github/workflows/doc-links.yml
name: Validate Doc Links

on:
  push:
    paths: ['claude/**/*.md']

jobs:
  validate:
    runs-on: ubuntu-latest
    steps:
      - name: Check internal links
        run: |
          # Validate all markdown links resolve
          # Check issue references exist
          # Warn on orphaned docs
```

### Proposed: ROADMAP ↔ Project Sync

When ROADMAP.md changes:
1. Parse Active Work table
2. Ensure each item has a GitHub Issue
3. Ensure issue is in correct Project status column
4. Report discrepancies

---

## Views & Dashboards

### GitHub Project Views

| View | Purpose | Grouping | Filters |
|------|---------|----------|---------|
| **Sprint Board** | Daily work | Status columns | None (all active) |
| **By Area** | Subsystem focus | Area field | Exclude Done |
| **Research Queue** | What needs exploration | Status | `label:research` |
| **Roadmap** | Timeline view | Milestone/Target Date | Has date |
| **Stale Items** | Items needing attention | None | No activity > 14 days |

### Custom Fields for Project

| Field | Type | Values | Purpose |
|-------|------|--------|---------|
| Status | Single-select | Backlog, Research, Ready, In Progress, Review, Done | Workflow stage |
| Area | Single-select | Database, MVCC, Transactions, Indexes, Schema, Storage, Memory, Concurrency, Primitives | Subsystem |
| Estimate | Single-select | XS, S, M, L, XL | Effort sizing |
| Priority | Single-select | P0, P1, P2, P3 | Urgency |
| Design Doc | Text | URL/path | Link to design |
| Target | Iteration | Sprint dates | Planning |

### ROADMAP.md Enhanced Format

```markdown
## Current Phase: [Phase Name]

> [Phase description and goals]

### Active Work

| Status | Item | Issue | Design | Area | Est | Notes |
|--------|------|-------|--------|------|-----|-------|
| 🚧 | Feature X | [#42](url) | [Design](path) | Storage | M | In PR review |

### Up Next (Prioritized)

| Priority | Item | Issue | Prereqs | Est |
|----------|------|-------|---------|-----|
| P0 | Critical Y | [#43](url) | #42 | L |

### Backlog

[Grouped by Area with rough priorities]

### Completed This Phase

| Item | Issue | Completed | Impact |
|------|-------|-----------|--------|
| Feature Z | [#40](url) | 2026-01-20 | Enabled X capability |
```

---

## Developer's Daily Guide

### Morning Routine

```
1. Open Rider (workspace loads last project)
2. Terminal: `claude` (starts session)
3. Claude automatically shows:
   - "Good morning! Current phase: Telemetry"
   - "You have 2 items In Progress: #42, #45"
   - "⚠️ #42 has no activity for 5 days"
4. Pick up where you left off or ask "what should I focus on?"
```

### Starting New Feature

```
Developer: "I want to work on query caching"

Claude: "I found these related items:
  - Idea: ideas/database-engine/QueryCaching.md (created Dec 2024)
  - No research doc yet
  - No GitHub issue

  This seems like it needs research first. Should I:
  1. Create a research doc and GitHub issue?
  2. Just capture more thoughts in the idea doc?
  3. Something else?"

Developer: "Create research and issue"

Claude: [Creates research/database-engine/QueryCaching.md]
        [Creates GitHub issue #50 with label:research]
        [Links them together]
        [Adds to Project in Research column]

"Done! Research doc and issue #50 created.
 The research doc has starter questions based on the idea.
 Want me to help explore any specific aspect first?"
```

### Completing Feature

```
Developer: "Feature for #42 is done, PR merged"

Claude: "Excellent! Let me wrap this up:

  ✅ Issue #42 closed
  ✅ Moved design/QueryEngine.md → reference/
  ✅ Updated ROADMAP.md (marked complete, dated)

  Quick questions:
  1. Should I update any overview/ docs? This touched the query layer.
  2. Any architectural decisions worth an ADR?

  [If yes to 1]: Which sections? (shows relevant overview docs)
  [If yes to 2]: What was the key decision?"
```

### Weekly Review

```
Developer: "/weekly-review"

Claude: "📊 Week of Jan 20-25, 2026

COMPLETED:
  - #42 Query Engine foundation
  - #45 AccessControl telemetry

IN PROGRESS:
  - #47 PagedMMF telemetry (3 days active)

OPENED:
  - #50 Query Caching research

⚠️ ATTENTION:
  - ideas/AsyncCheckpoint.md hasn't been touched in 45 days
  - research/PersistentViews.md concluded but no design doc yet

📈 MOUNTAIN VIEW:
  - Backlog: 15 items (~6-8 weeks estimated)
  - Current phase: 60% complete
  - Largest items: Query System (XL), WAL (L)

SUGGESTED ACTIONS:
  1. Archive or promote AsyncCheckpoint idea
  2. Create design doc for PersistentViews or park it
  3. Break down Query System into smaller issues"
```

---

## Decisions Made

These questions were resolved during implementation:

1. **Iterations/Sprints:** → **Continuous flow** (not sprints)
2. **Milestones:** → **Phases** via project field (Telemetry, Query, WAL, Reliability, Infrastructure)
3. **Sub-issues:** → **Yes**, use sub-issues under Phase parent issues
4. **Notifications:** → Deferred for now (manual `/weekly-review` provides stale detection)

---

## Implementation Summary

The following has been implemented:

### GitHub Project Fields
- Status: Backlog → Research → Ready → In Progress → Review → Done
- Priority: P0-Critical, P1-High, P2-Medium, P3-Low
- Phase: Telemetry, Query, WAL, Reliability, Infrastructure
- Area: Database, MVCC, Transactions, Indexes, Schema, Storage, Memory, Concurrency, Primitives
- Estimate: XS, S, M, L, XL
- Design Doc: (text field for linking to claude/design/)
- Target: (date field for Roadmap view)

### Claude Code Skills
- `/dev-status` — Show current development status
- `/start-work #XX` — Begin work on an issue
- `/complete-work #XX` — Finish work, update artifacts
- `/create-issue` — Create new issue with all fields
- `/weekly-review` — Weekly progress summary
- `/mountain-view` — Full backlog analysis

### GitHub Actions
- `.github/workflows/project-sync.yml` — Auto-add issues, sync status on close/merge

### Archived
- `claude/ROADMAP.md` → `claude/archive/ROADMAP-2026-01.md`

---

## References

- [claude/README.md](claude/README.md) — Document lifecycle system
- [.claude/skills/](.claude/skills/) — Claude Code skills
- [GitHub Project](https://github.com/users/nockawa/projects/7) — Source of truth for work tracking
- [GitHub Projects Best Practices](https://docs.github.com/en/issues/planning-and-tracking-with-projects/learning-about-projects/best-practices-for-projects)
