# Claude Working Documents

This folder contains working documents for Typhon development, organized by lifecycle stage. These documents support collaboration between the developer and Claude Code.

---

## Folder Structure

```
claude/
├── README.md       # This file - explains the structure and workflow
├── ROADMAP.md      # Development roadmap and priorities
│
├── overview/       # Comprehensive architectural documentation (living)
│   ├── README.md             # Entry point: quick links, config, tuning
│   ├── 01-concurrency.md     # Locks, latches, thread-safety internals
│   ├── 02-execution.md       # UoW, transactions, durability modes
│   ├── 03-storage.md         # PagedMMF, caching, I/O pipeline
│   ├── 04-data.md            # MVCC, indexes, revision chains
│   └── 05-11...              # Query, schema, collections, strings, etc.
│
├── ideas/          # Quick thoughts, raw ideas (lightweight)
│   ├── SimpleIdea.md           # Document at root level
│   ├── database-engine/        # Category (mirrors project areas)
│   │   ├── QueryCaching.md     # Document in category
│   │   └── transactions/       # Subcategory
│   │       └── NestedTx.md
│   └── persistence/            # Another category
│       └── CacheStrategy.md
│
├── research/       # Studies, analysis, comparisons
│   ├── QuickStudy.md           # Single file for focused research
│   ├── database-engine/        # Same categories as ideas/
│   │   └── transactions/
│   └── persistence/
│       └── timeout/            # Directory for deep-dive (complex topic)
│           ├── README.md       # Entry point with overview
│           ├── 01-taxonomy.md  # Numbered parts
│           └── 02-patterns.md
│
├── design/         # Active feature designs (pre-implementation)
│   └── [same category structure]
│
├── reference/      # Architecture & guides (post-implementation)
│   └── [same category structure]
│
├── adr/            # Architecture Decision Records (immutable)
│   ├── README.md           # Index and template
│   └── NNN-short-title.md  # One decision per file
│
├── assets/         # Diagrams and visual assets
│   ├── src/               # D2 source files (editable)
│   ├── viewer.html        # Interactive pan-zoom viewer
│   └── *.svg              # Rendered diagrams
│
└── archive/        # Obsolete or superseded documents
```

**Key concepts:**
- **Categories** are directories that organize documents by topic area (e.g., `database-engine/`, `persistence/`)
- **Categories span all stages** - the same category structure exists in ideas/, research/, design/, reference/
- **Documents** can be single files or directories (for complex topics)
- Documents naturally move through stages while staying in their category
- **`overview/`** is special: it's the authoritative architectural reference, not part of the lifecycle flow

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
                              ▼                          ┌────────────┐
                       ┌───────────┐                     │  overview/ │ ◄── Living architectural
                       │  archive/ │                     └────────────┘     reference (separate)
                       └───────────┘
```

### Stage Descriptions

| Folder | Question | Content | Effort Level |
|--------|----------|---------|--------------|
| `overview/` | "How does Typhon work?" | Comprehensive architectural docs covering all engine aspects | Living - evolves with codebase |
| `ideas/` | "What if we...?" | Raw thoughts, quick captures, half-formed concepts | Low - just capture it |
| `research/` | "How should we...?" | Analysis, comparisons, exploring options, prototypes | Medium - thorough exploration |
| `design/` | "Here's how we will..." | Concrete implementation plans, ready to build | High - detailed and actionable |
| `reference/` | "Here's how it works..." | Feature-specific guides and API docs (narrow scope) | Living docs - kept current |
| `adr/` | "We decided X because..." | Architecture Decision Records — one decision per file, immutable | Created when key decisions are made |
| `assets/` | (visual) | Diagrams (D2 source + rendered SVG), interactive viewer | Generated alongside docs |
| `archive/` | "This was..." | Superseded designs, completed explorations, historical | Preserved but inactive |

### `overview/` vs `reference/` — When to Use Which

| Aspect | `overview/` | `reference/` |
|--------|-------------|--------------|
| **Scope** | Engine-wide systems (transactions, MVCC, storage) | Specific features or APIs |
| **Audience** | Someone learning Typhon architecture | Someone using a specific capability |
| **Structure** | Numbered series (01-11) covering entire engine | Category-based, feature-focused docs |
| **Lifecycle** | Always current, never archived | May be archived when feature changes |
| **Example** | "How MVCC works" (04-data.md) | "How to use String64 API" |

**Rule of thumb**: If it explains *how Typhon works internally*, it belongs in `overview/`. If it explains *how to use a feature*, it goes in `reference/`.

### When to Move Documents

| Trigger | Action |
|---------|--------|
| Idea needs serious exploration | `ideas/` → `research/` |
| Decision made on approach | `research/` → `design/` |
| Feature implemented | `design/` → `reference/` (if still relevant) or `archive/` |
| Document superseded | Any → `archive/` |
| Research concluded (no action) | `research/` → `archive/` |
| Deep architectural insight discovered | Update relevant `overview/` section |

**Note on `overview/`**: Documents don't "move to" overview — they start there or are written there directly. The overview series is the canonical architectural reference, continuously maintained rather than archived.

### Single File vs Directory Structure

Documents can be either a **single file** or a **directory with multiple files**. Choose based on complexity:

| Structure | When to Use | Example |
|-----------|-------------|---------|
| **Single file** | Simple topics, focused scope, fits in one doc | `research/CacheStrategy.md` |
| **Directory** | Complex topics, multiple facets, deep dives | `research/TimeoutInternals/` |

#### Directory Structure Convention

For complex topics, use this structure:

```
research/TopicName/
├── README.md              # Entry point: overview, navigation, key insights
├── 01-first-aspect.md     # Numbered for reading order
├── 02-second-aspect.md
├── 03-third-aspect.md
└── ...
```

**README.md serves as:**
- Executive summary of the entire topic
- Table of contents with links to all parts
- Key insights extracted for quick reference
- Guidance on how to navigate the series

**Numbered files (01-, 02-, ...):**
- Enforce logical reading order
- Allow easy insertion (01, 02, 02a, 03 or renumber)
- Each covers one distinct aspect of the topic

#### When to Expand to Directory

Start with a single file. Expand to a directory when:
- The file exceeds ~50KB or becomes hard to navigate
- The topic naturally splits into distinct sub-topics
- You need to reference specific parts independently
- Multiple people might work on different aspects

To expand: tell Claude "expand this into a directory structure" and confirm the proposed split.

### Categories

Categories are directories that organize documents by topic area across all lifecycle stages. They provide a consistent structure that mirrors the project's logical areas.

#### Why Categories?

| Benefit | Description |
|---------|-------------|
| **Consistency** | Same category in all stages - easy to find related docs |
| **Context** | Groups related work together |
| **Navigation** | Familiar structure matching project areas |
| **Future-ready** | Enables automated documentation generation from reference/ |

#### Category Conventions

- Categories are **cross-stage**: creating `database-engine/` adds it to ideas/, research/, design/, and reference/
- Category names use **kebab-case**: `database-engine`, `access-control`, `block-allocator`
- Categories can be **nested**: `misc/access-control/`, `database-engine/transactions/`
- Categories can (but don't have to) **mirror the C# project structure** for familiarity

#### Example Category Structure

Loosely mirroring `src/Typhon.Engine/`:
```
ideas/                          research/                       design/
├── database-engine/            ├── database-engine/            ├── database-engine/
│   ├── bptree/                 │   ├── bptree/                 │   └── ...
│   ├── transactions/           │   ├── transactions/
│   └── schema/                 │   └── schema/
├── persistence/                ├── persistence/
├── collections/                ├── collections/
└── misc/                       └── misc/
    ├── access-control/             ├── access-control/
    └── allocators/                 └── allocators/
```

#### Document Location Selection

When creating a new document, Claude will **always ask** where to place it:
1. Show existing categories as options
2. Include "Root level" option (no category)
3. Include "Other (specify path)" for new locations or deep nesting

Example prompt:
```
Where should this idea go?
○ ideas/ (root level)
○ ideas/database-engine/
○ ideas/database-engine/transactions/
○ ideas/persistence/
○ Other (specify path)
```

---

## Diagrams & Visual Assets

### Tooling

Diagrams are authored in **D2** (`claude/assets/src/*.d2`) and rendered to SVG:

```bash
# Render a single diagram
"/c/Program Files/D2/d2.exe" --theme 0 assets/src/my-diagram.d2 assets/my-diagram.svg

# Available themes: 0 (default), 1 (Neutral Grey), 3 (Flagship Terrastruct),
#   100-103 (Cool Classics), 104-105 (Mixed Berry), 200 (Terminal), 300 (Origami)
# Add --sketch for hand-drawn style
```

### Embedding in Markdown

**Recommended pattern: Clickable thumbnail → full SVG**

```markdown
<!-- Thumbnail links to SVG file — VS Code opens in tab, browser provides native zoom -->
<a href="../assets/typhon-uow-lifecycle-states.svg">
  <img src="../assets/typhon-uow-lifecycle-states.svg" width="1200"
       alt="UoW Lifecycle States">
</a>
<sub>D2 source: <code>assets/src/typhon-uow-lifecycle-states.d2</code> — open <code>assets/viewer.html</code> for interactive pan-zoom</sub>
```

**Embed width rule — use native label size as measuring stick:**

D2 renders node labels at 16px by default. To keep diagrams compact while still readable (~12px labels on screen), compute the embed width from the SVG's native `viewBox` width:

```
embed_width = min(floor(nativeWidth × 0.75), 1200)
```

The factor `0.75` = `12/16` — it scales D2's default 16px labels to render at 12px on screen (readable, compact). The 1200px cap prevents horizontal diagrams from overflowing the page.

To find the native width, check the SVG file's first line: `viewBox="0 0 <width> <height>"`.

| Native Width | Embed Width | Example |
|-------------|-------------|---------|
| 775 | 581 | `typhon-error-escalation` (narrow vertical) |
| 921 | 690 | `typhon-query-overview` (tall vertical) |
| 1261 | 945 | `typhon-dependency-flow` (moderate vertical) |
| 1354 | 1015 | `typhon-immediate-commit-sequence` |
| 1558 | 1168 | `typhon-checkpoint-pipeline` |
| ≥1600 | 1200 | All wide/horizontal diagrams |

**How it works across renderers:**
| Renderer | Thumbnail | Click behavior |
|----------|-----------|----------------|
| VS Code markdown preview | Inline at computed width | Opens SVG in a new editor tab (zoomable) |
| GitHub | Inline at computed width | Navigates to SVG file view (raw = zoomable) |
| Obsidian | Inline at computed width | Opens SVG in default viewer |
| DocFx / served site | Inline at computed width | Opens SVG in browser (Ctrl+Scroll zoom) |

**For full pan-zoom interaction:** open `claude/assets/viewer.html` directly in a browser — it has a diagram switcher, drag-to-pan, scroll-to-zoom, and keyboard shortcuts.

### Interactive Viewer

`assets/viewer.html` provides:
- **Pan**: Click and drag
- **Zoom**: Scroll wheel (zooms toward cursor)
- **Keyboard**: `F` fit, `R` reset 1:1, `+`/`-` zoom
- **Diagram switcher**: Top-left menu lists all available diagrams
- **URL parameter**: `?diagram=filename.svg` to deep-link

### Adding New Diagrams

1. Create D2 source in `assets/src/my-diagram.d2`
2. Render: `"/c/Program Files/D2/d2.exe" --theme 0 assets/src/my-diagram.d2 assets/my-diagram.svg`
3. Add entry to `DIAGRAMS` array in `viewer.html` (for the switcher menu)
4. Embed in markdown using patterns above

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

### Directory README.md Template (for complex topics)

Use this template for the README.md entry point in directory-based documents:

```markdown
# [Topic Name]

[One-line description of what this series covers]

## Overview

[2-3 paragraphs explaining the scope, purpose, and what readers will learn]

## Target Audience

- [Who should read this]
- [What background is helpful]

## Document Series

| Part | Title | Focus |
|------|-------|-------|
| [01](./01-first-topic.md) | **First Topic** | [Brief description] |
| [02](./02-second-topic.md) | **Second Topic** | [Brief description] |
| [03](./03-third-topic.md) | **Third Topic** | [Brief description] |

## Key Insights

[Extract the most important takeaways here for readers who want the summary]

### [Insight Category 1]

[Key point with brief explanation]

### [Insight Category 2]

[Key point with brief explanation]

## How to Use This Series

1. **Start with Part X** if you want [goal]
2. **Start with Part Y** if you're [situation]
3. **Jump to Part Z** for [specific need]

## Quick Reference

[Tables, cheat sheets, or frequently-needed information]

## Related

- [Links to related documents, code, or external resources]
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

### Trigger Phrases

Use these phrases to trigger document lifecycle actions. Claude will **always confirm** before making changes.

#### Creating New Documents

| Phrase | Action |
|--------|--------|
| `start new idea <name>` | Ask for location, then create with idea template |
| `start new research <name>` | Ask for location, then create with research template |
| `start new design <name>` | Ask for location, then create with design template |

**With explicit location** (skip location prompt):

| Phrase | Action |
|--------|--------|
| `start new idea <name> in <category>` | Create directly in specified category |
| `start new research <name> in persistence/timeout` | Create in nested category path |

**For complex topics** (directory structure with multiple files):

| Phrase | Action |
|--------|--------|
| `start new idea <name> --deep` | Ask for location, create directory with README.md |
| `start new research <name> --deep` | Ask for location, create directory + initial parts |
| `start new design <name> --deep in <category>` | Create deep structure in specified category |

#### Managing Categories

| Phrase | Action |
|--------|--------|
| `create category <name>` | Create category in all stages (ideas/, research/, design/, reference/) |
| `create category <parent>/<child>` | Create nested category (e.g., `misc/access-control`) |
| `list categories` | Show current category structure |

**Notes on categories:**
- Categories are created **cross-stage** by default (same structure in all lifecycle folders)
- Use kebab-case for category names: `database-engine`, `access-control`
- Categories can mirror C# project structure but don't have to

#### Promoting Documents

| Phrase | Action |
|--------|--------|
| `promote to research` | Move current doc/dir `ideas/` → `research/`, restructure to research template |
| `promote to design` | Move current doc/dir `research/` → `design/`, restructure to design template |
| `promote to reference` | Move current doc/dir `design/` → `reference/`, restructure to reference template |

#### Expanding & Restructuring

| Phrase | Action |
|--------|--------|
| `expand this to directory` | Convert single file to directory structure with README.md + parts |
| `add new part <title>` | Add a new numbered part to current directory |

#### Archiving Documents

| Phrase | Action |
|--------|--------|
| `archive this` | Move current doc/dir → `archive/` |
| `archive <name>` | Move specified doc/dir → `archive/` |

**Notes:**
- Natural language variations are understood (e.g., "let's promote this to design" ≈ `promote to design`)
- When creating documents, Claude **always asks for location** (unless you specify it explicitly with `in <category>`)
- When promoting, content is restructured to fit the target stage's template while preserving key information
- Both single files and directories can be promoted - the entire structure moves together
- Claude will always show what will happen and ask for confirmation before moving or creating files

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
| Explain how a *feature* works | `reference/` |
| Explain how the *engine* works | `overview/` |
| Keep an old doc for history | `archive/` |
