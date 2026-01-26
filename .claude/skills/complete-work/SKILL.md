---
name: complete-work
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

### 2. Check for Open PR

```bash
gh pr list --search "<number>" --json number,title,state,url
```

If there's an open PR linked to this issue:
- Warn the user that the PR should be merged first
- Ask if they want to proceed anyway

### 3. Close the Issue

```bash
gh issue close <number>
```

### 4. Update Project Status to Done

```bash
# Get the project item ID
gh project item-list 7 --owner nockawa --format json

# Update status to Done
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

If there's a feature branch that was merged:
```bash
git branch -d feature/<number>-name
```

Ask before deleting.

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
