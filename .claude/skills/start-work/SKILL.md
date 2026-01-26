---
name: start-work
description: Start working on a GitHub issue - updates status, creates branch, verifies design
argument-hint: [issue number or title]
---

# Start Work on a GitHub Issue

Prepare to work on an issue by updating its status, creating a branch, and verifying prerequisites.

## Input

$ARGUMENTS may contain:
- An **issue number** (e.g., `42` or `#42`) → proceed directly to the workflow
- A **title/text** (non-numeric) → offer to create a new issue with that title
- **Nothing** (empty) → show available issues to pick from, or offer to create one

## Handling No Issue Number

### Case 1: No arguments provided

Fetch Ready and Backlog items from the project:

```bash
gh project item-list 7 --owner nockawa --format json
```

Filter for items with Status = "Ready" or "Backlog". Then use `AskUserQuestion` to present a choice:

**Question:** "Which issue would you like to start working on?"
**Header:** "Issue"
**Options** (up to 4, prioritize Ready items first, then Backlog by priority):
- `#<number> - <title>` (description: "[Status] [Priority] [Area]") — for each candidate issue
- `Create a new issue` (description: "Launch the /create-issue workflow to define a new issue first")

If the user picks an existing issue, continue with the normal workflow below using that issue number.

If the user picks "Create a new issue", tell the user:
> No worries! Let's create an issue first. Please run `/create-issue` with your issue title, then come back with `/start-work <number>`.

### Case 2: Non-numeric argument (looks like a title)

If $ARGUMENTS is not empty and not a number (doesn't match `^\d+$` after stripping `#`), use `AskUserQuestion`:

**Question:** "It looks like you provided a title instead of an issue number. Would you like to:"
**Header:** "Action"
**Options:**
- `Create a new issue with this title` (description: "Will launch the create-issue workflow with the title '$ARGUMENTS'")
- `Search existing issues` (description: "Search for issues matching '$ARGUMENTS' to pick one")

If "Create a new issue": tell the user to run `/create-issue $ARGUMENTS`, then come back with `/start-work <number>`.

If "Search existing issues": run `gh issue list --repo nockawa/Typhon --search "$ARGUMENTS" --json number,title,state --limit 5` and present matching issues via `AskUserQuestion`.

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
