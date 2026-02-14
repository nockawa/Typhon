# Skill Helpers — Robust GitHub Project Patterns

> **Referenced by:** All skills that interact with the Typhon GitHub Project board.
> Skills should include: `**Project item lookup:** Read `.claude/skills/_helpers.md` for the robust pattern.`

## Why These Patterns Exist

This project runs on **Windows** with Claude Code's bash shell. Two things break consistently:

1. **Temp file paths** — `$SCRATCHPAD`, `/tmp/`, and Windows `C:\` paths all have cross-environment issues between bash and Python. **Always pipe `gh` output directly to Python** instead of redirecting to a file.
2. **Python encoding** — Python on Windows defaults to `cp1252`, which chokes on Unicode characters (`→`, emojis) commonly found in GitHub issue bodies. **Always set `PYTHONUTF8=1`** or pass `encoding='utf-8'` when opening files for writing.

## Pattern 1: Find a Project Item ID by Issue Number

This is the most common operation — given issue #N, find its project item ID for status updates.

```bash
# Pipe directly to Python — no temp file needed. Always use --limit 200 (default is 30).
gh project item-list 7 --owner nockawa --limit 200 --format json 2>&1 | python3 -c "
import json, sys
items = json.load(sys.stdin)['items']
for item in items:
    if item.get('content', {}).get('number') == int(sys.argv[1]):
        print(item['id'])
        sys.exit(0)
print('NOT_FOUND')
" <ISSUE_NUMBER>
```

## Pattern 2: Find Item ID and Current Status

```bash
gh project item-list 7 --owner nockawa --limit 200 --format json 2>&1 | python3 -c "
import json, sys
items = json.load(sys.stdin)['items']
for item in items:
    if item.get('content', {}).get('number') == int(sys.argv[1]):
        print(f'{item[\"id\"]}|{item.get(\"status\", \"unknown\")}')
        sys.exit(0)
print('NOT_FOUND|unknown')
" <ISSUE_NUMBER>
```

## Pattern 3: List Items by Status

```bash
gh project item-list 7 --owner nockawa --limit 200 --format json 2>&1 | python3 -c "
import json, sys
items = json.load(sys.stdin)['items']
statuses = sys.argv[1].split(',')
for item in items:
    if item.get('status') in statuses:
        n = item.get('content', {}).get('number', '?')
        t = item.get('title', 'untitled')
        s = item.get('status', '?')
        p = item.get('priority', '?')
        a = item.get('area', '?')
        print(f'#{n} | {s} | {p} | {a} | {t}')
" "Ready,Backlog"
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

## Pattern 5: Edit a GitHub Issue Body (checkbox update, status change, etc.)

GitHub issue bodies often contain Unicode characters. **Never hardcode or reconstruct the body** — always fetch, modify, and write back.

```bash
# Fetch body, modify with Python, write to temp file, update issue
gh issue view <ISSUE_NUMBER> --repo nockawa/Typhon --json body --jq ".body" 2>&1 | PYTHONUTF8=1 python3 -c "
import sys, tempfile
body = sys.stdin.read()
# Example: check a checkbox for sub-issue #16
body = body.replace('- [ ] #16 ', '- [x] #16 ')
with tempfile.NamedTemporaryFile(mode='w', suffix='.md', encoding='utf-8', delete=False) as f:
    f.write(body)
    print(f.name)
" | while read tmpfile; do
  gh issue edit <ISSUE_NUMBER> --repo nockawa/Typhon --body-file "$tmpfile"
done
```

**Key points:**
- `PYTHONUTF8=1` forces Python to use UTF-8 regardless of Windows locale
- `tempfile.NamedTemporaryFile` creates a path accessible to both Python and `gh`
- Never reconstruct the body manually — Unicode chars, emojis, and special markdown will break

## Key Rules

1. **Always pipe `gh` output directly to Python** — temp file paths break across bash/Python/Windows boundaries
2. **NEVER use `grep` on JSON** — it's brittle to formatting changes. Use Python's `json` module
3. **Always use `--limit 200`** on `gh project item-list` — the default limit is 30, which misses items on larger boards
4. **Always check for `NOT_FOUND`** in the output before proceeding
5. **If NOT_FOUND and the issue should be on the board**, add it with `gh project item-add 7 --owner nockawa --url <issue_url>`, then re-fetch the project data and retry the lookup
6. **Always use `PYTHONUTF8=1`** when Python writes to files — Windows defaults to cp1252, which breaks on Unicode
7. **NEVER use relative paths in GitHub issue bodies** — GitHub renders issue bodies outside the repo context (e.g., on the project board), so relative links like `[text](claude/foo.md)` resolve to 404s. Always use absolute URLs:
   - **Files:** `https://github.com/nockawa/Typhon/blob/main/<path>` (e.g., `https://github.com/nockawa/Typhon/blob/main/claude/overview/10-errors.md`)
   - **Directories:** `https://github.com/nockawa/Typhon/tree/main/<path>` (e.g., `https://github.com/nockawa/Typhon/tree/main/claude/design/errors/`)
   - **Issues:** Use `#NN` shorthand (GitHub auto-links these correctly)
   - In design docs and local markdown files, relative paths are fine — they're rendered in the repo context

## Field Reference

| Field | Field ID |
|-------|----------|
| Project ID | `PVT_kwHOAud1ac4BNdCj` |
| Status | `PVTSSF_lAHOAud1ac4BNdCjzg8cXYI` |
| Priority | `PVTSSF_lAHOAud1ac4BNdCjzg8c8uQ` |
| Phase | `PVTSSF_lAHOAud1ac4BNdCjzg8c8uU` |
| Area | `PVTSSF_lAHOAud1ac4BNdCjzg8cX_E` |
| Estimate | `PVTSSF_lAHOAud1ac4BNdCjzg8cYEU` |
