# Skill Helpers — GitHub Operations Reference

> **Referenced by:** All skills that interact with GitHub Issues, PRs, and the Typhon Project board.

## Section 1: MCP Tools for Issues & PRs (Preferred)

The GitHub MCP server provides native tool calls that bypass all shell/encoding issues. **Always prefer MCP tools over `gh` CLI** for issue and PR operations.

### Fetch an Issue

Use `mcp__GitHub__get_issue` with:
- owner: `"nockawa"`
- repo: `"Typhon"`
- issue_number: `<number>`

Returns the full issue object including `number`, `title`, `body`, `state`, `labels`, `assignees`, etc.

### Create an Issue

Use `mcp__GitHub__create_issue` with:
- owner: `"nockawa"`
- repo: `"Typhon"`
- title: `"<title>"`
- body: `"<body>"`
- labels: `["<label1>", "<label2>"]`
- assignees: `["nockawa"]`

Returns the created issue object with its `number` and `html_url`.

### Update an Issue (close, edit body, change title, etc.)

Use `mcp__GitHub__update_issue` with:
- owner: `"nockawa"`
- repo: `"Typhon"`
- issue_number: `<number>`
- state: `"closed"` (to close) or `"open"` (to reopen)
- body: `"<new body>"` (to replace the entire body)
- title: `"<new title>"` (to change title)

**This replaces the old Pattern 5** (temp file + Python + `gh issue edit --body-file`). The body is passed directly as a string — no temp files, no encoding issues, no piping.

### List Issues

Use `mcp__GitHub__list_issues` with:
- owner: `"nockawa"`
- repo: `"Typhon"`
- state: `"open"` or `"closed"` or `"all"`
- per_page: `100` (default is 30)

Returns an array of issue objects.

### Search Issues (and PRs)

Use `mcp__GitHub__search_issues` with:
- q: `"repo:nockawa/Typhon <search terms>"`

Add `type:pr` to search PRs specifically, e.g.:
- q: `"repo:nockawa/Typhon type:pr <number>"` — find PRs mentioning an issue
- q: `"repo:nockawa/Typhon is:open <keywords>"` — search open issues

Returns search results with issue/PR objects.

### Create a Pull Request

Use `mcp__GitHub__create_pull_request` with:
- owner: `"nockawa"`
- repo: `"Typhon"`
- title: `"<title>"`
- body: `"<body>"`
- head: `"<branch-name>"`
- base: `"main"`

Returns the created PR object.

### List Commits

Use `mcp__GitHub__list_commits` with:
- owner: `"nockawa"`
- repo: `"Typhon"`

Returns an array of commit objects with sha, message, date, author, etc.

## Section 2: Project Board Operations (gh CLI Only)

The GitHub MCP server does **NOT** support GitHub Projects V2 API. All project board operations must use `gh` CLI.

### Why These Patterns Use Python Piping

This project runs on **Windows** with Claude Code's bash shell. Two things break with `gh` CLI:

1. **Temp file paths** — `$SCRATCHPAD`, `/tmp/`, and Windows `C:\` paths have cross-environment issues. **Always pipe `gh` output directly to Python**.
2. **Python encoding** — Python on Windows defaults to `cp1252`. **Always set `PYTHONUTF8=1`** when Python writes to files.

### Pattern 1: Find a Project Item ID by Issue Number

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

### Pattern 2: Find Item ID and Current Status

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

### Pattern 3: List Items by Status

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

### Pattern 4: Update Project Item Status

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

1. **MCP-first for issues & PRs** — Always use MCP tools (`mcp__GitHub__get_issue`, `mcp__GitHub__update_issue`, etc.) instead of `gh issue` / `gh pr` CLI commands
2. **`gh` CLI only for project board** — `gh project item-list`, `gh project item-edit`, `gh project item-add`, `gh project field-list` have no MCP equivalent
3. **Always pipe `gh project` output directly to Python** — temp file paths break across bash/Python/Windows boundaries
4. **NEVER use `grep` on JSON** — it's brittle to formatting changes. Use Python's `json` module
5. **Always use `--limit 200`** on `gh project item-list` — the default limit is 30, which misses items on larger boards
6. **Always check for `NOT_FOUND`** in the output before proceeding
7. **If NOT_FOUND and the issue should be on the board**, add it with `gh project item-add 7 --owner nockawa --url <issue_url>`, then re-fetch the project data and retry the lookup
8. **Always use `PYTHONUTF8=1`** when Python writes to files — Windows defaults to cp1252, which breaks on Unicode
9. **NEVER use relative paths in GitHub issue bodies** — GitHub renders issue bodies outside the repo context (e.g., on the project board), so relative links like `[text](claude/foo.md)` resolve to 404s. Always use absolute URLs:
   - **Files:** `https://github.com/nockawa/Typhon/blob/main/<path>` (e.g., `https://github.com/nockawa/Typhon/blob/main/claude/overview/10-errors.md`)
   - **Directories:** `https://github.com/nockawa/Typhon/tree/main/<path>` (e.g., `https://github.com/nockawa/Typhon/tree/main/claude/design/errors/`)
   - **Issues:** Use `#NN` shorthand (GitHub auto-links these correctly)
   - In design docs and local markdown files, relative paths are fine — they're rendered in the repo context

## Field Reference

| Field | Field ID |
|-------|----------|
| Project ID | `PVT_kwHOAud1ac4BNdCj` |
| Status | `PVTSSF_lAHOAud1ac4BNdCjzg8cXYI` |
| Priority | `PVTSSF_lAHOAud1ac4BNdCjzg8ctaQ` |
| Phase | `PVTSSF_lAHOAud1ac4BNdCjzg8ctaU` |
| Area | `PVTSSF_lAHOAud1ac4BNdCjzg8cktA` |
| Estimate | `PVTSSF_lAHOAud1ac4BNdCjzg8cYEU` |
