# Skill Helpers — Robust GitHub Project Patterns

> **Referenced by:** All skills that interact with the Typhon GitHub Project board.
> Skills should include: `**Project item lookup:** Read `.claude/skills/_helpers.md` for the robust pattern.`

## Why These Patterns Exist

The `gh project item-list` command returns **large JSON** (~50KB+). On Windows, piping this directly to Python or other tools can fail due to pipe buffer limits. The patterns below avoid this by always writing to a file first.

## Pattern 1: Find a Project Item ID by Issue Number

This is the most common operation — given issue #N, find its project item ID for status updates.

```bash
# Step 1: Save project data to a temp file (avoids pipe buffer issues)
gh project item-list 7 --owner nockawa --format json > "$TEMP_FILE"

# Step 2: Extract the item ID using Python (reads from file, not pipe)
python -c "
import json, sys
with open(sys.argv[1]) as f:
    items = json.load(f)['items']
for item in items:
    if item.get('content', {}).get('number') == int(sys.argv[2]):
        print(item['id'])
        sys.exit(0)
print('NOT_FOUND')
" "$TEMP_FILE" <ISSUE_NUMBER>
```

**Where `$TEMP_FILE`** is any writable path. Prefer the Claude Code scratchpad directory when available. If unsure, use `%TEMP%\gh-project-items.json` on Windows or `/tmp/gh-project-items.json` on Unix.

## Pattern 2: Find Item ID and Current Status

```bash
python -c "
import json, sys
with open(sys.argv[1]) as f:
    items = json.load(f)['items']
for item in items:
    if item.get('content', {}).get('number') == int(sys.argv[2]):
        print(f'{item[\"id\"]}|{item.get(\"status\", \"unknown\")}')
        sys.exit(0)
print('NOT_FOUND|unknown')
" "$TEMP_FILE" <ISSUE_NUMBER>
```

## Pattern 3: List Items by Status

```bash
python -c "
import json, sys
with open(sys.argv[1]) as f:
    items = json.load(f)['items']
statuses = sys.argv[2].split(',')
for item in items:
    if item.get('status') in statuses:
        n = item.get('content', {}).get('number', '?')
        t = item.get('title', 'untitled')
        s = item.get('status', '?')
        p = item.get('priority', '?')
        a = item.get('area', '?')
        print(f'#{n} | {s} | {p} | {a} | {t}')
" "$TEMP_FILE" "Ready,Backlog"
```

## Pattern 4: Update Project Item Status

Once you have the item ID (from Pattern 1), update the status:

```bash
gh project item-edit --project-id PVT_kwHOAud1ac4BNdCj --id <ITEM_ID> \
  --field-id PVTSSF_lAHOAud1ac4BNdCjzg8cXYI \
  --single-select-option-id <STATUS_OPTION_ID>
```

Status option IDs:
- Backlog: `11d8e01f`
- Research: `6aea77c6`
- Ready: `303600de`
- In Progress: `a0a7aab6`
- Review: `fadead67`
- Done: `12503e99`

## Key Rules

1. **NEVER pipe `gh project item-list` output directly to another command** — always redirect to a file first
2. **NEVER use `grep` on JSON** — it's brittle to formatting changes. Use Python's `json` module
3. **Always check for `NOT_FOUND`** in the output before proceeding
4. **Reuse the temp file** if multiple lookups are needed in the same skill invocation — don't re-fetch

## Field Reference

| Field | Field ID |
|-------|----------|
| Project ID | `PVT_kwHOAud1ac4BNdCj` |
| Status | `PVTSSF_lAHOAud1ac4BNdCjzg8cXYI` |
| Priority | `PVTSSF_lAHOAud1ac4BNdCjzg8c8uQ` |
| Phase | `PVTSSF_lAHOAud1ac4BNdCjzg8c8uU` |
| Area | `PVTSSF_lAHOAud1ac4BNdCjzg8cX_E` |
| Estimate | `PVTSSF_lAHOAud1ac4BNdCjzg8cYEU` |
