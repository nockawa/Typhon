---
name: dev-status
description: Show current development status from GitHub Project
argument-hint: (no arguments)
---

# Show Typhon Development Status

Display the current state of work from the GitHub Project, including active items, phase progress, and items needing attention.

## What to Display

### 1. Current Phase

Query the GitHub Project for items with Phase field set to identify the active phase. Look for the parent Phase issue (title starts with "Phase:").

### 2. In Progress Items

Query items where Status = "In Progress". **Always pipe `gh project item-list` directly to Python** (see `.claude/skills/_helpers.md`):
```bash
gh project item-list 7 --owner nockawa --limit 200 --format json 2>&1 | python3 -c "
import json, sys
items = json.load(sys.stdin)['items']
for item in items:
    s = item.get('status', '')
    if s == 'In Progress':
        n = item.get('content', {}).get('number', '?')
        t = item.get('title', 'untitled')
        p = item.get('priority', '?')
        a = item.get('area', '?')
        e = item.get('estimate', '?')
        print(f'#{n} | {s} | {p} | {a} | {e} | {t}')
"
```

Parse the output to filter and format. For each In Progress item, show:
- Issue number and title
- Area, Priority, Estimate
- Branch (if mentioned in issue body)
- Design Doc (if field is set)

### 3. Ready Items

Query items where Status = "Ready" - these are ready to be picked up next.

### 4. Items Needing Attention

Flag issues that:
- Have Status = "In Progress" but no recent activity (check issue last updated date)
- Have Status = "Research" for more than 14 days
- Are P0-Critical and not In Progress

### 5. Mountain View Summary

Calculate totals:
- Count of items by Status
- Sum of estimates (XS=1, S=2, M=4, L=8, XL=16 points)
- Breakdown by Phase

## Output Format

```
☀️ Typhon Development Status

📌 Current Phase: [Phase Name]
   Parent: #XX — N/M sub-issues done

🚧 In Progress (N):
   #XX Title [Area, Priority, Estimate]
       Branch: feature/XX-name (if known)
       Design: path/to/design.md (if set)

📋 Ready (N):
   #XX Title [Area, Priority, Estimate] — has design / needs design

🔬 Research (N):
   #XX Title [Priority, Estimate]

⚠️ Needs Attention:
   #XX reason (e.g., "no activity for N days", "P0 not started")

📈 Mountain View:
   Backlog: N items | Research: N | Ready: N | In Progress: N
   Estimated effort: ~X points remaining
   By Phase: Telemetry N, Query N, ...

💡 Suggested: [Pick up #XX or continue #YY]
```

## Implementation

Use `gh project item-list 7 --owner nockawa --limit 200 --format json` to get all items, then filter and format the output.

Check issue activity with `gh issue view <number> --json updatedAt`.
