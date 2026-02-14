---
name: complete-task
description: Complete work on a GitHub issue - close issue, update artifacts, prompt for doc updates
argument-hint: [issue number]
---

# Complete Work on a GitHub Issue

Finalize work on an issue by closing it, updating project status, and ensuring all artifacts are properly maintained.

## Input

$ARGUMENTS should contain the issue number (e.g., "42" or "#42").

If no issue number provided, use `AskUserQuestion` to ask which issue to complete.

## Workflow

### 1. Verify Issue State

```bash
gh issue view <number> --json number,title,state,labels,body
```

Confirm the issue is currently open and has been worked on.

### 2. Check PR and Branch State

**Step 2a: Check for existing PRs** (open or merged):

```bash
gh pr list --search "<number>" --state all --json number,title,state,url,headRefName
```

**Step 2b: Detect unmerged feature branch** (if no merged PR found):

```bash
# Find the feature/fix branch for this issue
git branch --list "feature/<number>*" --list "fix/<number>*"

# If a branch exists, check if it has commits ahead of main
git rev-list --count main..<branch_name>
```

**Decision matrix:**

| PR State | Branch State | Action |
|----------|-------------|--------|
| Merged PR exists | — | Proceed normally (happy path) |
| Open PR exists | — | Warn: "PR should be merged first" → ask to proceed or wait |
| No PR | Branch has commits ahead of main | Warn: "Branch has N unmerged commits" → offer to create PR |
| No PR | No feature branch / branch is even with main | Proceed normally (work may have been committed directly to main) |

**If no PR and branch has unmerged commits**, ask:

```
Question: "Branch '<branch_name>' has N commits not yet merged to main. A PR should typically be created and merged before completing the task. What would you like to do?"
Header: "PR"
Options:
  - Create a PR now (Recommended) (description: "I'll create a PR from <branch_name> to main, then continue after it's merged")
  - Skip PR (description: "Proceed without a PR — I'll handle merging manually")
  - Cancel (description: "Stop — I'll create and merge the PR first, then re-run /complete-task")
```

**If "Create a PR now":**
- Push the branch if not already pushed: `git push -u origin <branch_name>`
- Create the PR using `gh pr create` with a summary derived from the issue title and body
- **Stop here** — report the PR URL and tell the user to merge it, then re-run `/complete-task`
- Do NOT proceed to close the issue or update status yet

**If "Skip PR":**
- Proceed with the rest of the workflow (close issue, update status, etc.)

**If "Cancel":**
- Stop and report that no changes were made

### 3. Close the Issue

```bash
gh issue close <number>
```

### 4. Update Project Status to Done

**Project item lookup:** Read `.claude/skills/_helpers.md` for the robust patterns.

```bash
# Step 1: Find the item ID by piping directly to Python (no temp files)
gh project item-list 7 --owner nockawa --limit 200 --format json 2>&1 | python3 -c "
import json, sys
items = json.load(sys.stdin)['items']
for item in items:
    if item.get('content', {}).get('number') == int(sys.argv[1]):
        print(item['id'])
        sys.exit(0)
print('NOT_FOUND')
" <issue_number>

# Step 2: Update status to Done (using the item ID from step 1)
gh project item-edit --project-id PVT_kwHOAud1ac4BNdCj --id <item_id> \
  --field-id PVTSSF_lAHOAud1ac4BNdCjzg8cXYI \
  --single-select-option-id 12503e99  # "Done"
```

### 5. Check for Design Doc

Look for design doc in:
- Issue body (links to `claude/design/`)
- Project item's "Design Doc" field

If a design doc exists, ask the user:
```
Question: "The design doc 'claude/design/FeatureName.md' was used. What should happen to it?"
Header: "Design Doc"
Options:
  - Move to reference/ (implementation complete, useful for future reference)
  - Move to archive/ (outdated or superseded)
  - Keep in design/ (still relevant for ongoing work)
```

### 6. Suggest Overview Updates

Check if this issue might affect overview documentation:

```bash
# Get issue labels/area to determine relevant docs
```

Suggest potentially affected overview docs based on Area field:
- Database → `claude/overview/02-execution.md`, `claude/overview/04-data.md`
- Transactions → `claude/overview/02-execution.md`
- MVCC → `claude/overview/04-data.md`
- Indexes → `claude/overview/04-data.md`
- Schema → `claude/overview/04-data.md`
- Storage → `claude/overview/03-storage.md`
- Memory → `claude/overview/08-resources.md`
- Concurrency → `claude/overview/01-concurrency.md`
- Primitives → `claude/overview/11-utilities.md`
- Telemetry → `claude/overview/09-observability.md`

Ask:
```
Question: "These overview docs might need updates based on this work:"
  • claude/overview/XX-topic.md

"Would you like to review any of them?"
Options:
  - Yes, open them for review
  - Skip for now
```

### 7. Check for ADR Need

Ask:
```
Question: "Did this work involve a significant architectural decision that should be documented?"
Header: "ADR"
Options:
  - Yes, create an ADR (I'll help draft it)
  - No, no significant decisions
```

If yes, help create an ADR in `claude/adr/` following the template.

### 8. Clean Up Branch (Optional)

Check if a local feature/fix branch still exists for this issue:

```bash
git branch --list "feature/<number>*" --list "fix/<number>*"
```

If a branch exists **and** a merged PR was found in step 2 (meaning the code is safely on `main`):

```
Question: "Branch '<branch_name>' was merged via PR. Delete the local branch?"
Header: "Branch"
Options:
  - Yes, delete it (description: "git branch -d <branch_name>")
  - Keep it (description: "Leave the branch for now")
```

If "Yes": `git branch -d <branch_name>`

If no merged PR was found (user chose "Skip PR" in step 2), do **not** offer to delete — the branch may contain the only copy of the work.

### 9. Report Summary

```
Completing #<number>: <title>

✅ Issue closed
✅ Status: In Progress → Done
📄 Design doc: claude/design/<Name>.md
   → Moved to reference/ (or kept/archived)

📚 Overview docs reviewed: (list or "skipped")
📝 ADR created: claude/adr/0XX-decision.md (or "none")
🌿 Branch cleaned up: feature/<number>-name (or "kept")

Work complete! 🎉
```

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
