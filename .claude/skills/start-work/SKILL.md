---
name: start-work
description: Start working on a GitHub issue - updates status, creates branch, verifies design
argument-hint: [issue number]
---

# Start Work on a GitHub Issue

Prepare to work on an issue by updating its status, creating a branch, and verifying prerequisites.

## Input

$ARGUMENTS should contain the issue number (e.g., "42" or "#42").

If no issue number provided, use `AskUserQuestion` to ask which issue to start.

## Workflow

### 1. Fetch Issue Details

```bash
gh issue view <number> --json number,title,body,labels
```

### 2. Check Design Doc

Look for design doc reference in:
- Issue body (links to `claude/design/`)
- Project item's "Design Doc" field

If no design doc exists and this is an enhancement (not a bug fix):
- Ask: "This issue has no design doc. Should I create one, or proceed without?"
- If yes, create `claude/design/<IssueName>.md` using the design template

### 3. Update Project Status

Get the project item ID and update Status to "In Progress":

```bash
# Get item ID from project
gh project item-list 7 --owner nockawa --format json | grep -A5 '"number":<issue_number>'

# Update status field
gh project item-edit --project-id PVT_kwHOAud1ac4BNdCj --id <item_id> \
  --field-id PVTSSF_lAHOAud1ac4BNdCjzg8cXYI \
  --single-select-option-id a0a7aab6  # "In Progress"
```

### 4. Create Branch

Determine branch name based on issue type:
- Enhancement/Feature: `feature/<number>-short-name`
- Bug: `fix/<number>-short-name`

```bash
git checkout -b feature/<number>-short-name
```

### 5. Update Design Doc (if exists)

Add branch reference to the design doc:
```markdown
**GitHub Issue:** #<number>
**Branch:** `feature/<number>-short-name`
**Status:** In progress
```

### 6. Report Summary

```
Starting work on #<number>: <title>

✅ Design doc exists: claude/design/<Name>.md (or "No design doc")
✅ Status updated: <old> → In Progress
✅ Branch created: feature/<number>-short-name
✅ Checked out branch

Ready to implement!
```

## Status Field Option IDs

For reference:
- Backlog: `11d8e01f`
- Research: `6aea77c6`
- Ready: `303600de`
- In Progress: `a0a7aab6`
- Review: `fadead67`
- Done: `12503e99`
