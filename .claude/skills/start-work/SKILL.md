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
- `Create a new issue` (description: "I'll help you create one right now")

If the user picks an existing issue, continue with the normal workflow below using that issue number.

If the user picks "Create a new issue", proceed to **Inline Issue Creation** below.

### Case 2: Non-numeric argument (looks like a title)

If $ARGUMENTS is not empty and not a number (doesn't match `^\d+$` after stripping `#`), use `AskUserQuestion`:

**Question:** "It looks like you provided a title instead of an issue number. Would you like to:"
**Header:** "Action"
**Options:**
- `Create a new issue with this title` (description: "I'll create '#$ARGUMENTS' and start work on it")
- `Search existing issues` (description: "Search for issues matching '$ARGUMENTS' to pick one")

If "Create a new issue": proceed to **Inline Issue Creation** with the title pre-filled.

If "Search existing issues": run `gh issue list --repo nockawa/Typhon --search "$ARGUMENTS" --json number,title,state --limit 5` and present matching issues via `AskUserQuestion`.

## Inline Issue Creation

When an issue needs to be created, do it inline rather than redirecting the user to `/create-issue`.

Follow the `/create-issue` skill workflow directly:

1. **Gather info** — Use `AskUserQuestion` to collect:
   - Title (if not already provided from $ARGUMENTS)
   - Description (ask the user to describe what needs to be done)
   - Type labels, Area, Priority, Phase, Estimate (use the same questions as `/create-issue`)

2. **Create the issue** — Execute the full `/create-issue` workflow:
   ```bash
   gh issue create --repo nockawa/Typhon --title "TITLE" --body "DESCRIPTION" --label "LABELS" --assignee nockawa
   ```

3. **Add to project and set fields** — Follow `/create-issue` steps 3-5

4. **Continue** — Once the issue is created, continue with the normal `/start-work` workflow below using the new issue number.

This makes `/start-work` a one-stop command: describe what you want to work on, and Claude creates the issue + starts work in a single flow.

## Workflow

### 1. Fetch Issue Details

```bash
gh issue view <number> --json number,title,body,labels
```

### 2. Check Design Doc

Look for design doc reference in the issue body (links to `claude/design/`).

If no design doc exists and this is an enhancement (not a bug fix):
- Ask: "This issue has no design doc. Should I create one, or proceed without?"
- If yes, create `claude/design/<IssueName>.md` using the design template
- Add a link to the design doc in the issue body under "Related Documents"

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

### 4. Branch Creation

**Base branch:** Always create feature branches from `main` (GitHub Flow).

Determine the recommended branch name based on issue type:
- Enhancement/Feature: `feature/<number>-short-name`
- Bug: `fix/<number>-short-name`

Then ask the user how they want the branch created:

**Question:** "How should the branch be created?"
**Header:** "Branch"
**Options:**
- `Claude creates it` (description: "I'll run git checkout -b <branch-name> from main")
- `Rider Open Task` (description: "I'll skip — use Alt+Shift+N in Rider to create branch via Open Task for issue #<number>")
- `Skip branch` (description: "Don't create a branch yet, I'll handle it later")

**If "Claude creates it":**
```bash
# Ensure we're on main and up-to-date
git checkout main
git pull origin main
git checkout -b feature/<number>-short-name
```

**If "Rider Open Task":**
Report the recommended branch name for reference but don't create it. The user will use Rider's `Tools > Tasks & Contexts > Open Task` (Alt+Shift+N) to select the issue and let Rider create the branch + context switch.

**If "Skip branch":**
Just report the recommended name for later use.

### 5. Update Design Doc (if exists)

Add branch reference to the design doc:
```markdown
**GitHub Issue:** #<number>
**Branch:** `feature/<number>-short-name`
**Status:** In progress
```

Note: If the user chose "Rider Open Task" or "Skip branch", still write the recommended branch name in the design doc. Update it later if Rider uses a different name.

### 6. Report Summary

```
Starting work on #<number>: <title>

✅ Design doc: claude/design/<Name>.md (or "No design doc — skipped")
✅ Status updated: <old> → In Progress
🌿 Branch: feature/<number>-short-name
   └─ Created by Claude / Use Rider Open Task (Alt+Shift+N) / Skipped

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

## Field Reference

### Project ID
- `PVT_kwHOAud1ac4BNdCj`

### Status Field
- Field ID: `PVTSSF_lAHOAud1ac4BNdCjzg8cXYI`

### Priority Field
- Field ID: `PVTSSF_lAHOAud1ac4BNdCjzg8c8uQ`

### Phase Field
- Field ID: `PVTSSF_lAHOAud1ac4BNdCjzg8c8uU`

### Area Field
- Field ID: `PVTSSF_lAHOAud1ac4BNdCjzg8cX_E`

### Estimate Field
- Field ID: `PVTSSF_lAHOAud1ac4BNdCjzg8cYEU`

