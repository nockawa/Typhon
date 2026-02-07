---
name: create-issue
description: Create a GitHub issue and add it to the Typhon dev project
argument-hint: [title] or leave empty for interactive mode
---

# Create GitHub Issue for Typhon

Create a GitHub issue and add it to the "Typhon dev" project (project #7).

## Input provided by user

$ARGUMENTS

## Required information

You need to gather the following information. If NOT provided in the arguments above, use the `AskUserQuestion` tool to ask the user:

1. **Title** (required): A clear, concise issue title
2. **Description** (required): What needs to be done and why
3. **Type labels** (optional): One or more of: `bug`, `enhancement`, `documentation`, `performance`, `refactoring`, `testing`, `technical-debt`, `question`
4. **Area** (optional - project field): Database, MVCC, Transactions, Indexes, Schema, Storage, Memory, Concurrency, Primitives
5. **Priority** (optional): P0-Critical, P1-High, P2-Medium, P3-Low
6. **Phase** (optional): Telemetry, Query, WAL, Reliability, Infrastructure
7. **Estimate** (optional): T-shirt size: XS, S, M, L, XL

## Gathering information

- If the user provided a clear title and description in their arguments, proceed directly
- If information is missing or unclear, use `AskUserQuestion` to ask for it
- For labels, area, priority, phase, and estimate, offer them as options in `AskUserQuestion` with multi-select for labels
- Be conversational and helpful when asking questions

### Example questions to ask:

If title is missing:
```
Question: "What should the issue title be?"
Options: Let user type freely (no predefined options needed - just ask in text)
```

If description is missing:
```
Question: "Can you describe what needs to be done?"
```

For type labels (if not provided):
```
Question: "What type of issue is this?"
Header: "Type"
Options: bug, enhancement, documentation, performance, refactoring, testing, technical-debt
MultiSelect: true
```

For area (if not provided):
```
Question: "Which area of the codebase does this primarily affect?"
Header: "Area"
Options:
  - Database (DatabaseEngine, ComponentTable, API)
  - Transactions (Transaction, TransactionChain)
  - MVCC (Revisions, snapshot isolation)
  - Indexes (B+Tree implementations)
  - Schema (Component definitions, attributes)
  - Storage (PagedMMF, segments, persistence)
  - Memory (Allocators, memory blocks)
  - Concurrency (Locks, AccessControl)
  - Primitives (String64, Variant, collections)
MultiSelect: false
```

For priority (if not provided):
```
Question: "What priority level?"
Header: "Priority"
Options:
  - P0-Critical (blocking, needs immediate attention)
  - P1-High (important, should be done soon)
  - P2-Medium (normal priority)
  - P3-Low (nice to have, do when time permits)
```

For phase (if relevant):
```
Question: "Is this part of a specific development phase?"
Header: "Phase"
Options:
  - Telemetry (observability, metrics, diagnostics)
  - Query (query engine, filtering, sorting)
  - WAL (write-ahead logging, recovery)
  - Reliability (error handling, resilience)
  - Infrastructure (tooling, CI/CD, docs)
  - None (standalone issue)
```

For estimate (if not provided):
```
Question: "How would you estimate the effort?"
Header: "Estimate"
Options:
  - XS (trivial, < 1 hour)
  - S (small, half a day)
  - M (medium, 1-2 days)
  - L (large, 3-5 days)
  - XL (extra large, 1+ week)
```

## Creating the issue

Once you have the required information:

### Step 1: Create the issue

```bash
gh issue create --repo nockawa/Typhon --title "TITLE" --body "DESCRIPTION" --label "LABELS" --assignee nockawa
```

Note: Issues are always assigned to `nockawa` by default.

### Step 2: Get the issue URL from the output

### Step 3: Add to project

```bash
gh project item-add 7 --owner nockawa --url ISSUE_URL
```

### Step 4: Get the project item ID

**Project item lookup:** Read `.claude/skills/_helpers.md` for the robust pattern. **Never pipe `gh project item-list` directly** — always redirect to a temp file first, then parse with Python.

```bash
# Save to temp file, then find the item ID
gh project item-list 7 --owner nockawa --format json > "$SCRATCHPAD/project-items.json"

python -c "
import json, sys
with open(sys.argv[1]) as f:
    items = json.load(f)['items']
for item in items:
    if item.get('content', {}).get('number') == int(sys.argv[2]):
        print(item['id'])
        sys.exit(0)
print('NOT_FOUND')
" "$SCRATCHPAD/project-items.json" <issue_number>
```

### Step 5: Set project fields

Use the field IDs below to set the appropriate fields:

```bash
# Set multiple fields for the item
gh project item-edit --project-id PVT_kwHOAud1ac4BNdCj --id ITEM_ID \
  --field-id FIELD_ID --single-select-option-id OPTION_ID
```

## Field Reference

### Project ID
- `PVT_kwHOAud1ac4BNdCj`

### Status Field
- Field ID: `PVTSSF_lAHOAud1ac4BNdCjzg8cXYI`
- Options:
  - Backlog: `11d8e01f`
  - Research: `6aea77c6`
  - Ready: `303600de`
  - In Progress: `a0a7aab6`
  - Review: `fadead67`
  - Done: `12503e99`

### Priority Field
- Field ID: `PVTSSF_lAHOAud1ac4BNdCjzg8c8uQ`
- Options: Look up via `gh project field-list 7 --owner nockawa --format json`

### Phase Field
- Field ID: `PVTSSF_lAHOAud1ac4BNdCjzg8c8uU`
- Options: Look up via `gh project field-list 7 --owner nockawa --format json`

### Area Field
- Field ID: `PVTSSF_lAHOAud1ac4BNdCjzg8cX_E`
- Options: Look up via `gh project field-list 7 --owner nockawa --format json`

### Estimate Field
- Field ID: `PVTSSF_lAHOAud1ac4BNdCjzg8cYEU`
- Options: Look up via `gh project field-list 7 --owner nockawa --format json`

### Target Date Field
- Field ID: `PVTF_lAHOAud1ac4BNdCjzg8c8uc`
- Set with: `--date "2026-02-15"`

## Output

After creating the issue, report back to the user with:
- Issue number and title
- Link to the issue
- Confirmation it was added to "Typhon dev" project
- The fields that were set (Priority, Phase, Area, Estimate)

## Example interaction

User: `/create-issue Add support for spatial indexing`

Claude: *Asks for description, area, priority, and estimate*

User: *Provides answers*

Claude: *Creates issue, adds to project, sets fields, reports success*

```
✅ Issue #15 created: "Add support for spatial indexing"
   🔗 https://github.com/nockawa/Typhon/issues/15
   📋 Added to "Typhon dev" project
   👤 Assigned to: nockawa
   🎯 Priority: P2-Medium
   📦 Phase: Query
   🗂️ Area: Indexes
   📏 Estimate: L (large)
   🏷️ Labels: enhancement, performance
```
