---
name: complete-subtask
description: Complete a sub-issue of an umbrella issue - close it, check parent checkbox, update design doc
argument-hint: [sub-issue number]
---

# Complete a Sub-Issue (Subtask)

Mark a sub-issue as done within an umbrella issue workflow. This is the lightweight counterpart to `/complete-work` — it handles subtask completion without branch cleanup, PR checks, or ADR prompts.

**Typical workflow:**
```
/start-work #36          ← umbrella issue, creates branch
... implement #37 ...
/complete-subtask #37    ← this skill
... implement #38 ...
/complete-subtask #38    ← this skill
... implement #39, #40 ...
/complete-subtask #39
/complete-subtask #40
/complete-work #36       ← closes umbrella, merges PR, cleans up
```

## Input

$ARGUMENTS should contain the sub-issue number (e.g., `37` or `#37`).

If no argument provided, use `AskUserQuestion` to ask which sub-issue to complete.

## Workflow

### 1. Fetch Sub-Issue Details

```bash
gh issue view <number> --json number,title,state,body
```

Confirm the issue is open. If already closed, report and exit.

### 2. Detect Parent (Umbrella) Issue

Search the sub-issue body for a parent reference. Common patterns:
- `**GitHub Issue:** #NN (umbrella)` or `Sub-issues: #37, #38, ...`
- `Parent: #NN`
- `Part of #NN`
- Any `#NN` reference where NN is a different issue

**Auto-detection strategy:**

```bash
# Check if the sub-issue body references a parent
gh issue view <number> --json body --jq '.body'
```

Look for patterns like:
- A line containing "umbrella" with an issue number
- A line containing "parent" with an issue number
- A "Sub-issues:" or "Part of" reference

If multiple candidates are found, or none are found, ask:

```
Question: "Which issue is the parent/umbrella for #<number>?"
Header: "Parent"
Options:
  - #<candidate1> - <title> (if candidates found)
  - Enter manually (description: "I'll type the parent issue number")
```

### 3. Close the Sub-Issue

```bash
gh issue close <number>
```

### 4. Update Project Status to Done

```bash
# Get the project item ID for the sub-issue
gh project item-list 7 --owner nockawa --format json
```

Find the item matching the sub-issue number, then update:

```bash
gh project item-edit --project-id PVT_kwHOAud1ac4BNdCj --id <item_id> \
  --field-id PVTSSF_lAHOAud1ac4BNdCjzg8cXYI \
  --single-select-option-id 12503e99  # "Done"
```

### 5. Check Checkbox in Parent Issue

Fetch the parent issue body, find the checkbox line referencing this sub-issue, and check it.

```bash
# Get parent issue body
gh issue view <parent_number> --json body --jq '.body'
```

Find a line matching the pattern `- [ ]` that contains `#<sub_number>` (the sub-issue number). Replace `- [ ]` with `- [x]` on that line.

**Checkbox detection patterns** (match any):
- `- [ ] ... #37 ...` (explicit issue reference)
- `- [ ] ... #37: ...` (reference with colon)
- `- [ ] **#37** ...` (bold reference)
- `- [ ] ... Sub-issue #37 ...` (text reference)

If the checkbox line also contains a description, preserve it. Only change `[ ]` to `[x]`.

**Update the parent issue body:**

```bash
gh issue edit <parent_number> --body "<updated_body>"
```

**Important:** Use a HEREDOC or temp file to pass the body to avoid shell escaping issues with markdown content:

```bash
# Write updated body to temp file, then:
gh issue edit <parent_number> --body-file /tmp/parent_body.md
```

If no matching checkbox is found, report it but don't fail — the parent may use a different format.

### 6. Update Design Doc Status (if exists)

Look for a design doc reference in the sub-issue body (links to `claude/design/`).

If found, read the design doc and update its status line:

```
# BEFORE:
**Status:** Ready for implementation

# AFTER:
**Status:** Implemented
```

Only update the `**Status:**` line in the YAML-like header at the top of the doc. Don't modify anything else.

If no design doc is found or referenced, skip this step silently.

### 7. Report Summary

```
Completed sub-issue #<number>: <title>

  Issue: #<number> closed
  Project: Status → Done
  Parent: #<parent> checkbox checked
  Design: claude/design/<path> → Status: Implemented (or "no design doc")

Parent #<parent> progress: X/Y sub-issues complete
```

For the progress line, count checked vs total checkboxes in the parent body that reference sub-issues.

## Edge Cases

### Sub-issue has no parent
If no parent can be detected and the user doesn't provide one:
- Still close the sub-issue and update project status
- Skip checkbox update
- Report that no parent was found

### Parent has no checkboxes
If the parent issue body doesn't have checkboxes:
- Still close the sub-issue and update project status
- Report that no checkbox was found to check

### Design doc not in expected format
If the design doc doesn't have a `**Status:**` line:
- Skip the update
- Report that the design doc format wasn't recognized

## Status Field Option IDs

For reference:
- Backlog: `11d8e01f`
- Research: `6aea77c6`
- Ready: `303600de`
- In Progress: `a0a7aab6`
- Review: `fadead67`
- Done: `12503e99`

## Field IDs

- Status: `PVTSSF_lAHOAud1ac4BNdCjzg8cXYI`
- Project ID: `PVT_kwHOAud1ac4BNdCj`
