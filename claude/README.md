# Claude Working Documents

This folder contains working documents for Typhon development, organized by lifecycle stage. These documents support collaboration between the developer and Claude Code.

---

## Folder Structure

```
claude/
├── README.md       # This file - explains the structure and workflow
├── ROADMAP.md      # Development roadmap and priorities
│
├── ideas/          # Quick thoughts, raw ideas (lightweight)
├── research/       # Studies, analysis, comparisons
├── design/         # Active feature designs (pre-implementation)
├── reference/      # Architecture & guides (post-implementation)
└── archive/        # Obsolete or superseded documents
```

---

## Document Lifecycle

```
┌─────────┐     ┌──────────┐     ┌────────┐     ┌───────────┐
│  ideas/ │ ──► │ research/│ ──► │ design/│ ──► │ reference/│
└─────────┘     └──────────┘     └────────┘     └───────────┘
     │               │                │               │
     │               │                │               │
     └───────────────┴────────────────┴───────────────┘
                              │
                              ▼
                       ┌───────────┐
                       │  archive/ │
                       └───────────┘
```

### Stage Descriptions

| Folder | Question | Content | Effort Level |
|--------|----------|---------|--------------|
| `ideas/` | "What if we...?" | Raw thoughts, quick captures, half-formed concepts | Low - just capture it |
| `research/` | "How should we...?" | Analysis, comparisons, exploring options, prototypes | Medium - thorough exploration |
| `design/` | "Here's how we will..." | Concrete implementation plans, ready to build | High - detailed and actionable |
| `reference/` | "Here's how it works..." | Documentation of implemented features, architecture | Living docs - kept current |
| `archive/` | "This was..." | Superseded designs, completed explorations, historical | Preserved but inactive |

### When to Move Documents

| Trigger | Action |
|---------|--------|
| Idea needs serious exploration | `ideas/` → `research/` |
| Decision made on approach | `research/` → `design/` |
| Feature implemented | `design/` → `reference/` (if still relevant) or `archive/` |
| Document superseded | Any → `archive/` |
| Research concluded (no action) | `research/` → `archive/` |

---

## Document Templates

### ideas/ Template

```markdown
# [Idea Title]

**Date:** YYYY-MM-DD
**Status:** Raw idea | Worth exploring | Parked

## The Idea

[1-3 paragraphs describing the concept]

## Why This Might Matter

- [Benefit 1]
- [Benefit 2]

## Open Questions

- [ ] Question 1?
- [ ] Question 2?

## Related

- [Link to related docs or issues]
```

### research/ Template

```markdown
# [Research Topic]

**Date:** YYYY-MM-DD
**Status:** In progress | Concluded | Moved to design
**Outcome:** [One-line summary of conclusion, filled in when done]

## Context

[Why are we researching this? What problem are we solving?]

## Questions to Answer

1. Question 1?
2. Question 2?

## Analysis

### Option A: [Name]

**Description:** [How it works]

**Pros:**
- Pro 1
- Pro 2

**Cons:**
- Con 1
- Con 2

### Option B: [Name]

[Same structure]

## Recommendation

[Which option and why]

## Next Steps

- [ ] Action item 1
- [ ] Action item 2
```

### design/ Template

```markdown
# [Feature Name] Design

**Date:** YYYY-MM-DD
**Status:** Draft | Ready for implementation | In progress | Implemented
**Branch:** `feature/xxx` (when work begins)

## Summary

[2-3 sentences: what this feature does and why]

## Goals

- Goal 1
- Goal 2

## Non-Goals

- [Explicitly out of scope]

## Design

### Overview

[High-level description with diagram if helpful]

### Data Structures

[Key types, schemas, storage]

### API / Interface

[Public API, method signatures]

### Implementation Details

[Key algorithms, edge cases, error handling]

## Testing Strategy

- [ ] Unit tests for X
- [ ] Integration tests for Y

## Open Questions

- [ ] Unresolved question?

## References

- [Link to research doc]
- [Link to related design]
```

### reference/ Template

```markdown
# [Topic Name]

**Last Updated:** YYYY-MM-DD
**Applies to:** [Version or component]

## Overview

[What this document covers]

## [Main Sections as Appropriate]

[Content organized for readers who need to understand or use this]

## Examples

[Code examples, usage patterns]

## See Also

- [Related reference docs]
```

---

## Working with Claude Code

### Starting a New Feature

1. **Capture the idea** → Create a file in `ideas/` (or just discuss it)
2. **Add to roadmap** → If worth pursuing, add to `ROADMAP.md` backlog
3. **Research if needed** → Create `research/[topic].md` for complex decisions
4. **Design it** → Create `design/[feature].md` when ready to plan implementation
5. **Implement** → Reference the design doc during development
6. **Document** → Move/update to `reference/` for ongoing documentation

### Asking Claude to Help

| Task | Example Prompt |
|------|----------------|
| Capture an idea | "Add an idea about X to ideas/" |
| Research options | "Research approaches for X, create a study in research/" |
| Create a design | "Create a design doc for X based on our research" |
| Update roadmap | "Mark X as complete and add Y to the backlog" |
| Archive old docs | "Move the old X design to archive, it's superseded by Y" |

### File Naming Conventions

- Use `PascalCase` for multi-word names: `ComponentCollection.md`
- Be descriptive but concise: `QueryOptimization.md` not `Query.md`
- Prefix with category if helpful: `UserGuide-ComponentLifecycle.md`

---

## Maintenance

### Regular Cleanup

Periodically review:
- `ideas/` - Archive or promote stale ideas
- `research/` - Archive concluded research, promote decisions to design
- `design/` - Archive implemented designs or move to reference
- `reference/` - Keep current with actual implementation

### Linking to Roadmap

The `ROADMAP.md` file should link to relevant documents:
```markdown
| Item | Design Doc | Status |
|------|------------|--------|
| Query System | [QueryOptimization](design/QueryOptimization.md) | In progress |
```

---

## Quick Reference

| I want to... | Create in... |
|--------------|--------------|
| Jot down a quick thought | `ideas/` |
| Compare approaches for a problem | `research/` |
| Document how I'll build something | `design/` |
| Explain how something works | `reference/` |
| Keep an old doc for history | `archive/` |
