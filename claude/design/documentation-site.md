# Documentation Site Design

**Status:** Design
**Created:** 2026-02-14
**Category:** Infrastructure / Developer Experience
**Predecessor:** [Documentation Tooling Research](../ideas/misc/documentation-tooling/README.md)

---

## TL;DR

> 💡 Build a unified documentation site at **https://nockawa.github.io/Typhon/** using **DocFX** (already deployed), directly referencing `claude/` content via `docfx.json`. The site has three tiers: **User-facing** (overview, guides, API reference, benchmarks), **Internals** (ADRs, design docs, architecture reference), and **auto-generated** (API from source, benchmark charts from CI). No content duplication — `claude/` files are the single source of truth.

---

## Table of Contents

- [Goals & Non-Goals](#goals--non-goals)
- [Architecture Overview](#architecture-overview)
- [Site Structure](#site-structure)
- [Content Strategy](#content-strategy)
- [Front-Matter Strategy](#front-matter-strategy)
- [DocFX Configuration](#docfx-configuration)
- [Diagram Integration](#diagram-integration)
- [Benchmark Integration](#benchmark-integration)
- [Build & Deployment Pipeline](#build--deployment-pipeline)
- [Customization & Theming](#customization--theming)
- [Landing Page](#landing-page)
- [Migration Plan](#migration-plan)
- [Open Questions](#open-questions)

---

## Goals & Non-Goals

### Goals

1. **Unified documentation site** — Single URL (`nockawa.github.io/Typhon/`) serving both external users and contributors
2. **Direct reference** — `claude/` files are the source of truth, included by DocFX with no duplication
3. **Tiered visibility** — Clear separation between user-facing docs and internals
4. **API reference** — Auto-generated from C# XML comments via DocFX Roslyn extraction (already working)
5. **Benchmark dashboard** — Integrated benchmark trend charts from CI runs
6. **Architecture diagrams** — D2-rendered SVGs embedded in documentation pages
7. **Automated deployment** — Push to `main` triggers build and deploy to GitHub Pages
8. **Low maintenance** — Writing docs in `claude/` automatically updates the site; no manual sync step

### Non-Goals

- Version-specific documentation (premature — Typhon isn't published yet)
- Custom React/JS components (stick with DocFX template system)
- Publishing raw ideas or early research (these stay internal to `claude/ideas/`)
- Blog/changelog section (can be added later)
- Search beyond DocFX's built-in client-side search

---

## Architecture Overview

```
                    ┌─────────────────────────────────────────────┐
                    │              GitHub Pages                   │
                    │      nockawa.github.io/Typhon/              │
                    └──────────────────┬──────────────────────────┘
                                       │
                              ┌────────┴──────────┐
                              │   DocFX Build     │
                              │   (GitHub Actions)│
                              └────────┬──────────┘
                                       │
              ┌────────────────────────┼────────────────────────┐
              │                        │                        │
     ┌────────┴─────────┐    ┌─────────┴──────────┐   ┌─────────┴────────────┐
     │  API Metadata    │    │  Conceptual Docs   │   │  Benchmark Results   │
     │  (Roslyn → YAML) │    │  (claude/ → DocFX) │   │  (/benchmark → DocFX)│
     │  from src/       │    │  overview, adr,    │   │  committed to repo   │
     └──────────────────┘    │  reference, design │   └──────────────────────┘
                             └────────────────────┘
```

### Key Decisions

| Decision | Choice | Rationale |
|----------|--------|-----------|
| SSG Tool | **DocFX** | Already deployed; native C# API support; handles unsafe/generics/attributes. See [research](../ideas/misc/documentation-tooling/README.md). |
| Content source | **Direct reference** via `docfx.json` | No duplication; `claude/` is single source of truth |
| Hosting | **GitHub Pages** via `gh-pages` branch | Free, fast CDN, already configured |
| Benchmarks | **Local skill → `/benchmark/`** | Benchmark results committed to repo; included as DocFX content |
| Diagrams | **D2 → SVG** | 33 diagrams already rendered; embedded via `<img>` tags in markdown |
| Template | DocFX `modern` + custom CSS | Best available DocFX theme; custom overrides for branding |

---

## Site Structure

### Top-Level Navigation (Tab Bar)

```
┌─────────────┬──────────────┬───────────────┬────────────────┬──────────────┐
│  Overview   │   Guides     │  API Docs     │  Internals     │  Benchmarks  │
└─────────────┴──────────────┴───────────────┴────────────────┴──────────────┘
```

### Detailed Site Map

```
/                               → Landing page (doc/index.md)
│
├── /overview/                  → Architecture overview series
│   ├── index.md               → "What is Typhon?" + quick-start (claude/overview/README.md)
│   ├── concurrency.md         → 01-concurrency.md
│   ├── execution.md           → 02-execution.md
│   ├── storage.md             → 03-storage.md
│   ├── data.md                → 04-data.md
│   ├── query.md               → 05-query.md
│   ├── durability.md          → 06-durability.md
│   ├── backup.md              → 07-backup.md
│   ├── resources.md           → 08-resources.md
│   ├── observability.md       → 09-observability.md
│   ├── errors.md              → 10-errors.md
│   └── utilities.md           → 11-utilities.md
│
├── /guides/                    → User-facing how-to guides
│   ├── getting-started.md     → New! Installation, first component, first transaction
│   ├── component-lifecycle.md → From claude/reference/UserGuide-ComponentLifecycle.md
│   ├── threading-patterns.md  → Extracted from claude/overview/README.md threading section
│   ├── configuration.md       → Extracted from claude/overview/README.md tuning section
│   └── typhon-shell.md        → From claude/reference/TyphonShell/
│
├── /api/                       → Auto-generated API reference (DocFX metadata)
│   └── (generated YAML → HTML)
│
├── /internals/                 → For contributors and architects
│   ├── architecture.md        → claude/reference/Architecture.md
│   ├── adr/                   → Architecture Decision Records
│   │   ├── index.md           → ADR index (claude/adr/README.md)
│   │   └── NNN-*.md           → Individual ADRs (claude/adr/*)
│   ├── design/                → Active design documents
│   │   └── *.md               → claude/design/*.md
│   └── reference/             → Implementation reference
│       ├── concurrency/       → claude/reference/concurrency/
│       ├── errors/            → claude/reference/errors/
│       ├── observability/     → claude/reference/observability/
│       ├── resources/         → claude/reference/resources/
│       └── storage/           → claude/reference/storage/
│
└── /benchmarks/                → Performance results
    └── (from /benchmark/ directory in repo)
```

### Content Mapping: `claude/` → Site Sections

| `claude/` Directory | Site Section | Visibility | Notes |
|---------------------|-------------|------------|-------|
| `claude/overview/` | `/overview/` | **Public** | Core architecture series — primary educational content |
| `claude/reference/UserGuide-*` | `/guides/` | **Public** | User-facing guides |
| `claude/reference/TyphonShell/` | `/guides/` | **Public** | Shell documentation |
| `claude/reference/Architecture.md` | `/internals/` | **Public (Internals)** | Deep architecture reference |
| `claude/reference/concurrency/` | `/internals/reference/` | **Public (Internals)** | Implementation details |
| `claude/reference/errors/` | `/internals/reference/` | **Public (Internals)** | Error system details |
| `claude/reference/observability/` | `/internals/reference/` | **Public (Internals)** | Telemetry details |
| `claude/reference/resources/` | `/internals/reference/` | **Public (Internals)** | Resource system details |
| `claude/reference/storage/` | `/internals/reference/` | **Public (Internals)** | Storage layer details |
| `claude/adr/` | `/internals/adr/` | **Public (Internals)** | Architecture Decision Records |
| `claude/design/` | `/internals/design/` | **Public (Internals)** | Design specifications |
| `claude/ideas/` | *(excluded)* | **Private** | Not mature enough for public docs |
| `claude/research/` | *(excluded)* | **Private** | Internal analysis, not polished for public |
| `claude/archive/` | *(excluded)* | **Private** | Obsolete documents |
| `claude/ops/` | *(excluded)* | **Private** | Operational guides, internal tooling |
| `claude/assets/` | Resource files | **Public** | SVG diagrams referenced by docs |

---

## Front-Matter Strategy

### Rationale

All published `claude/` markdown files should include a lightweight YAML front-matter block. This unlocks three key DocFX capabilities that don't work without it:

1. **Cross-referencing (`uid`)** — Allows `<xref:overview-data>` links from API XML comments to conceptual docs, and between markdown files. Links are validated at build time.
2. **Page titles (`title`)** — Controls the browser tab title and SEO `<title>` tag. Without it, DocFX may fall back to the filename, producing poor search results.
3. **Search snippets (`description`)** — Populates SEO meta description and improves on-site search result quality.

### Front-Matter Template

Every published markdown file gets this minimal block (4 lines):

```yaml
---
uid: overview-concurrency
title: Concurrency & Synchronization
description: AccessControl, latches, deadlines, and thread safety in Typhon
---
```

- No other properties are needed. `ms.date`, `ms.author`, `_layout`, etc. are irrelevant for this project.
- The `---` delimiters are hidden by GitHub's markdown renderer, so these files still look clean on GitHub.
- Front-matter must be the very first thing in the file (no BOM, no leading whitespace).

### UID Naming Convention

UIDs use a flat `{section}-{topic}` pattern. No `typhon-` prefix — everything in this site is Typhon.

| Section | Pattern | Examples |
|---------|---------|----------|
| Overview series | `overview-{topic}` | `overview-concurrency`, `overview-storage`, `overview-data` |
| ADRs | `adr-{NNN}` | `adr-001`, `adr-003`, `adr-023` |
| User guides | `guide-{topic}` | `guide-getting-started`, `guide-component-lifecycle` |
| Reference docs | `ref-{topic}` | `ref-architecture`, `ref-page-cache`, `ref-access-control` |
| Design docs | `design-{topic}` | `design-query-engine`, `design-wal` |

For subdirectory-based docs (e.g., `reference/concurrency/`), use a hyphenated topic: `ref-access-control-family`, `ref-error-model`.

### Cross-Reference Usage

Once UIDs are in place, cross-references work in two directions:

**From markdown to markdown:**
```markdown
This implements the pattern described in [ADR-003](xref:adr-003).
See the [storage engine overview](xref:overview-storage) for context.
```

**From C# XML comments to conceptual docs:**
```csharp
/// <summary>
/// Implements MVCC snapshot isolation.
/// See <xref:overview-data> for the full data engine overview.
/// </summary>
public class Transaction { }
```

**From conceptual docs to API types:**
```markdown
The <xref:Typhon.Engine.Transaction> class implements this pattern.
```

(API types get their UIDs automatically from DocFX's Roslyn extraction — these are fully-qualified type names.)

### Scope

Front-matter is added only to files that will be published to the doc site. Files in `claude/ideas/`, `claude/research/`, `claude/archive/`, and `claude/ops/` are excluded from the site and do **not** need front-matter.

| Directory | Gets Front-Matter |
|-----------|------------------|
| `claude/overview/*.md` | Yes |
| `claude/adr/*.md` | Yes |
| `claude/reference/**/*.md` (published subset) | Yes |
| `claude/design/**/*.md` (published subset) | Yes |
| `claude/ideas/`, `claude/research/`, `claude/archive/`, `claude/ops/` | No |
| `claude/README.md` | No (not published) |

---

## DocFX Configuration

### Updated `docfx.json`

The key change: the `build.content` array adds entries that reference `claude/` files directly, mapping them into the site structure using the `dest` property.

```json
{
  "metadata": [
    {
      "src": [
        {
          "files": ["**.csproj"],
          "src": "../src"
        }
      ],
      "dest": "api",
      "filter": "filterConfig.yml",
      "includePrivateMembers": false
    }
  ],
  "build": {
    "content": [
      {
        "files": ["*.md", "*.yml"],
        "exclude": ["live/**"]
      },
      {
        "files": [
          "overview/README.md",
          "overview/01-concurrency.md",
          "overview/02-execution.md",
          "overview/03-storage.md",
          "overview/04-data.md",
          "overview/05-query.md",
          "overview/06-durability.md",
          "overview/07-backup.md",
          "overview/08-resources.md",
          "overview/09-observability.md",
          "overview/10-errors.md",
          "overview/11-utilities.md"
        ],
        "src": "../claude",
        "dest": "overview"
      },
      {
        "files": [
          "reference/UserGuide-ComponentLifecycle.md",
          "reference/TyphonShell/**"
        ],
        "src": "../claude",
        "dest": "guides"
      },
      {
        "files": [
          "adr/**"
        ],
        "src": "../claude",
        "dest": "internals/adr"
      },
      {
        "files": [
          "reference/Architecture.md",
          "reference/concurrency/**",
          "reference/errors/**",
          "reference/observability/**",
          "reference/resources/**",
          "reference/storage/**"
        ],
        "src": "../claude",
        "dest": "internals/reference"
      },
      {
        "files": [
          "design/**"
        ],
        "src": "../claude",
        "dest": "internals/design",
        "exclude": [
          "design/documentation-site.md"
        ]
      }
    ],
    "resource": [
      {
        "files": ["images/**"]
      },
      {
        "files": ["assets/*.svg"],
        "src": "../claude",
        "dest": "assets"
      }
    ],
    "template": [
      "default",
      "modern",
      "template"
    ],
    "globalMetadata": {
      "_appName": "Typhon",
      "_appTitle": "Typhon",
      "_appFaviconPath": "images/favicon.ico",
      "_appLogoPath": "images/typhon.svg",
      "_appFooter": "Copyright (c) Loïc Baumann",
      "_enableSearch": true,
      "pdf": false
    },
    "dest": "live"
  }
}
```

### Key Configuration Points

1. **`includePrivateMembers: false`** — Changed from `true`. Public API docs should only show the public surface. Internal members are documented in the Internals section via narrative docs.

2. **`src` + `dest` mapping** — Each content group maps `claude/` subdirectories to their site location. DocFX resolves relative paths from the `src` directory.

3. **Resource files** — SVG diagrams from `claude/assets/` are copied as resources so image references in markdown work.

4. **Excludes** — This design doc itself is excluded from the site. `ideas/`, `research/`, `archive/`, `ops/` directories are not listed and thus excluded.

### TOC Files

DocFX requires `toc.yml` files for navigation. These will be created in `doc/` to control the site structure.

**`doc/toc.yml`** (top-level navigation):
```yaml
- name: Overview
  href: overview/
- name: Guides
  href: guides/
- name: API Reference
  href: api/
- name: Internals
  href: internals/
- name: Benchmarks
  href: benchmarks/
```

**`doc/overview/toc.yml`** (overview section sidebar):
```yaml
- name: What is Typhon?
  href: README.md
- name: Architecture
  items:
  - name: Concurrency & Synchronization
    href: 01-concurrency.md
  - name: Execution System
    href: 02-execution.md
  - name: Storage Engine
    href: 03-storage.md
  - name: Data Engine
    href: 04-data.md
  - name: Query Engine
    href: 05-query.md
  - name: Durability & Recovery
    href: 06-durability.md
  - name: Backup & Restore
    href: 07-backup.md
  - name: Resource Management
    href: 08-resources.md
  - name: Observability
    href: 09-observability.md
  - name: Error Handling
    href: 10-errors.md
  - name: Shared Utilities
    href: 11-utilities.md
```

**`doc/guides/toc.yml`**:
```yaml
- name: Getting Started
  href: getting-started.md
- name: Component Lifecycle
  href: UserGuide-ComponentLifecycle.md
- name: Typhon Shell
  href: TyphonShell/README.md
```

**`doc/internals/toc.yml`**:
```yaml
- name: Architecture Reference
  href: reference/Architecture.md
- name: Architecture Decisions (ADRs)
  href: adr/
- name: Design Documents
  href: design/
- name: Implementation Reference
  items:
  - name: Concurrency Primitives
    href: reference/concurrency/
  - name: Error System
    href: reference/errors/
  - name: Observability
    href: reference/observability/
  - name: Resource Management
    href: reference/resources/
  - name: Storage Layer
    href: reference/storage/
```

**`doc/internals/adr/toc.yml`**:
```yaml
- name: ADR Index
  href: README.md
- name: Core Architecture
  items:
  - name: "001: Three-Tier API Hierarchy"
    href: 001-three-tier-api-hierarchy.md
  - name: "002: ECS Data Model"
    href: 002-ecs-data-model.md
  # ... (all 32 ADRs listed)
```

---

## Diagram Integration

### Current State

- 33 D2 diagrams in `claude/assets/src/*.d2`
- Rendered SVGs in `claude/assets/*.svg`
- Markdown files reference them via relative paths: `../assets/typhon-*.svg`

### Challenge

When `claude/overview/01-concurrency.md` references `../assets/typhon-concurrency.svg`, DocFX needs to resolve this path correctly. Since the file is mapped from `claude/overview/` to `doc/overview/` in the output, the relative path `../assets/` needs to resolve to the resource files.

### Solution

The resource mapping in `docfx.json` copies `claude/assets/*.svg` to the output under `assets/`. The markdown image references use `../assets/` which, from the `overview/` directory, correctly resolves to the `assets/` directory at the site root level.

If path resolution doesn't work cleanly, we have two fallback options:

1. **Wrapper TOC approach** — Create thin wrapper `.md` files in `doc/` that include the `claude/` content. This gives full control over paths but adds a layer of indirection.

2. **Build script fixup** — A pre-build script that adjusts image paths in copied content. Adds complexity but keeps markdown files pristine.

The preferred approach is to test the direct reference first and only fall back if needed.

---

## Benchmark Integration

### Current State & Transition

The legacy `benchmark.yml` GitHub Actions workflow (using `rhysd/github-action-benchmark`) is being deprecated. Benchmarks are now run locally via Claude's `/benchmark` skill, with results committed to the `/benchmark` directory in the repository.

This is a better model for documentation: benchmark results are version-controlled alongside the code, and DocFX can include them directly as content — no separate `gh-pages` coordination needed.

### Approach

Benchmark results in `/benchmark` are included as DocFX content, rendered as regular documentation pages within the site's navigation. This means:

- Benchmark pages get full DocFX chrome (header, sidebar, search)
- Results are versioned with the code (git history tracks performance over time)
- No workflow coordination issues — everything goes through the single DocFX build
- Markdown-formatted benchmark results render natively

### DocFX Integration

Add `/benchmark` as a content source in `docfx.json`:

```json
{
  "files": ["**/*.md"],
  "src": "../benchmark",
  "dest": "benchmarks"
}
```

The benchmarks section TOC (`doc/benchmarks/toc.yml`) will reference the files from `/benchmark` by their destination paths. The exact TOC structure depends on how the benchmark skill organizes its output (per-benchmark-class, per-date, summary page, etc.) and will be finalized once the skill is in use.

### Legacy Cleanup

Once the `/benchmark` skill is established:
- [ ] Delete `.github/workflows/benchmark.yml`
- [ ] Remove the `bench/` directory from `gh-pages` (if present)
- [ ] The `keep_files: true` setting in the doc deployment workflow becomes unnecessary for benchmark preservation (but is still harmless to keep)

---

## Build & Deployment Pipeline

### Updated GitHub Actions Workflow

```yaml
name: Build Documentation

on:
  push:
    branches: [main]
    paths:
      - doc/**
      - claude/overview/**
      - claude/adr/**
      - claude/reference/**
      - claude/design/**
      - claude/assets/*.svg
      - benchmark/**
      - src/**/*.cs
      - src/**/*.csproj
      - .github/workflows/build-documentation.yml
  pull_request:
    branches: [main]
    paths:
      - doc/**
      - claude/**
      - benchmark/**
      - src/**
  workflow_dispatch:

jobs:
  build-docs:
    runs-on: windows-latest
    steps:
      - uses: actions/checkout@v4

      - name: Setup .NET 10.0
        uses: actions/setup-dotnet@v4
        with:
          dotnet-version: 10.0.x

      - name: Install DocFX
        run: dotnet tool update -g docfx

      - name: Build DocFX
        working-directory: doc
        run: docfx docfx.json

      - name: Publish to GitHub Pages
        if: github.event_name == 'push' && github.ref == 'refs/heads/main'
        uses: peaceiris/actions-gh-pages@v4
        with:
          github_token: ${{ secrets.GITHUB_TOKEN }}
          publish_dir: doc/live
```

### Key Changes from Current Workflow

1. **Trigger on `main` branch** (not `new-persistence-layer`)
2. **Path triggers include `claude/` and `benchmark/`** — Changes to docs or benchmark results trigger a rebuild
3. **Updated action versions** (`actions/checkout@v4`, `actions/setup-dotnet@v4`)
4. **PR builds without publish** — PRs build to verify docs compile, but don't deploy
5. **No `keep_files`** — Benchmarks are now part of the DocFX build, not a separate `gh-pages` artifact

---

## Customization & Theming

### Scope

The DocFX `modern` template provides a clean, functional design. Customization should be minimal and focused:

1. **Landing page** — Custom hero section with project description and quick links
2. **Color scheme** — Match Typhon branding (currently has `typhon.svg` logo)
3. **Footer** — Copyright and GitHub link
4. **Code blocks** — Ensure C# syntax highlighting works well for unsafe/pointer code

### Custom CSS

Located in `doc/template/styles/main.css` (already exists). Additions:

```css
/* Landing page hero */
.hero-section {
  text-align: center;
  padding: 3rem 1rem;
  margin-bottom: 2rem;
}

.hero-section h1 {
  font-size: 2.5rem;
  margin-bottom: 0.5rem;
}

.hero-section .tagline {
  font-size: 1.2rem;
  color: var(--bs-secondary);
  margin-bottom: 2rem;
}

/* Feature cards on landing page */
.feature-grid {
  display: grid;
  grid-template-columns: repeat(auto-fit, minmax(250px, 1fr));
  gap: 1.5rem;
  margin: 2rem 0;
}

.feature-card {
  padding: 1.5rem;
  border: 1px solid var(--bs-border-color);
  border-radius: 8px;
}
```

### Template Overrides

The existing `doc/template/` directory supports DocFX template partial overrides. The `head.tmpl.partial` already exists for custom head content.

---

## Landing Page

### Design

The landing page (`doc/index.md`) should serve as the entry point for all audiences:

```markdown
---
_layout: landing
---

# Typhon Database Engine

Real-time, low-latency ACID database engine with microsecond-level performance.

## Key Features

- **ECS Data Model** — Entities are IDs, data lives in typed blittable structs
- **MVCC Transactions** — Snapshot isolation, optimistic concurrency, readers never block
- **Persistent B+Tree Indexes** — Automatic secondary indexes on component fields
- **Embedded** — No server process, runs in-process with your application

## Quick Links

| I want to... | Go to... |
|--------------|----------|
| Understand what Typhon is | [Overview](overview/) |
| Start using Typhon | [Getting Started Guide](guides/getting-started.md) |
| Browse the API | [API Reference](api/) |
| Understand design decisions | [Architecture Decision Records](internals/adr/) |
| See performance data | [Benchmarks](benchmarks/) |

## Who is Typhon for?

| Workload | Why Typhon Fits |
|----------|-----------------|
| Game servers | High-frequency updates, ECS-native model, transactional safety |
| Simulations | MVCC allows concurrent reads during world updates |
| Real-time systems | Microsecond reads, predictable latency, no GC pressure |
| Embedded applications | No separate server, runs in-process, single-file storage |
```

---

## Migration Plan

### Phase 1: Foundation (Core Setup)

**Goal:** DocFX builds successfully with `claude/` content included.

- [ ] Update `docfx.json` with new content mappings
- [ ] Create TOC files for all sections (`overview/`, `guides/`, `internals/`, `internals/adr/`)
- [ ] Test that `claude/overview/` files render correctly
- [ ] Test that diagram SVG references resolve
- [ ] Fix any broken cross-references between `claude/` docs
- [ ] Update `doc/index.md` landing page

### Phase 2: Front-Matter & Content Polish

**Goal:** All published files have front-matter; all content sections are navigable and look good.

- [ ] Add YAML front-matter (`uid`, `title`, `description`) to all `claude/overview/*.md` files (12 files)
- [ ] Add YAML front-matter to all `claude/adr/*.md` files (32 files + README)
- [ ] Add YAML front-matter to published `claude/reference/` files
- [ ] Add YAML front-matter to published `claude/design/` files
- [ ] Create `guides/getting-started.md` (new content — basic tutorial)
- [ ] Verify ADR rendering (all 32 ADRs)
- [ ] Verify reference doc rendering (concurrency, errors, observability, resources, storage)
- [ ] Verify design doc rendering
- [ ] Review and fix link paths across all included content
- [ ] Convert key inter-doc links to `xref:` syntax where beneficial

### Phase 3: Pipeline & Deployment

**Goal:** Automated build and deploy on push to `main`.

- [ ] Update `.github/workflows/build-documentation.yml` with new triggers and config
- [ ] Test build in CI (PR build without deploy)
- [ ] Deploy to `gh-pages`
- [ ] Delete legacy `.github/workflows/benchmark.yml`
- [ ] Clean up `bench/` directory from `gh-pages` branch if present

### Phase 4: Polish & Extras

**Goal:** Site feels professional and complete.

- [ ] Custom CSS refinements
- [ ] Landing page styling
- [ ] Review public API surface (adjust `filterConfig.yml` — exclude internals, test helpers)
- [ ] Add `robots.txt` and `sitemap.xml` (DocFX generates sitemap)
- [ ] Add GitHub edit links (DocFX supports `_gitContribute` metadata)

---

## Resolved Decisions

| Question | Decision | Rationale |
|----------|----------|-----------|
| `includePrivateMembers` | Set to `false` | Public API docs should show the public surface. Use `filterConfig.yml` to explicitly include specific internal types if needed (e.g., `AccessControl`). |
| Front-matter in `claude/` files | **Yes** — add `uid`, `title`, `description` | Enables cross-references, proper page titles, and search quality. See [Front-Matter Strategy](#front-matter-strategy). |
| UID naming convention | `{section}-{topic}` (no `typhon-` prefix) | Everything in the site is Typhon — the prefix is redundant noise. |
| D2 diagram rendering | Keep pre-rendered SVGs | Current workflow of `.d2` → render → commit `.svg` is well-established. No CI change needed. |

---

## Open Questions

### 1. Markdown Compatibility

DocFX uses Markdig (its own Markdown flavor). The `claude/` files use standard GitHub-Flavored Markdown. Most features overlap, but some differences:
- DocFX supports `> [!NOTE]`, `> [!WARNING]` admonitions (GFM uses `> **Note:**`)
- Mermaid diagrams (if any) need a DocFX plugin

**Recommendation:** Test the existing files first. Fix compatibility issues per-file as discovered. The D2→SVG diagrams avoid the Mermaid issue entirely.

### 2. Cross-Reference Links Between `claude/` Files

Many `claude/` files link to each other using relative paths (e.g., `../adr/001-three-tier-api-hierarchy.md`). When DocFX remaps these files to different output directories, these links may break.

**Recommendation:** After initial setup, run a link checker on the built site. Fix broken links either by:
- Adjusting the `dest` mapping to preserve directory relationships
- Adding DocFX redirects
- Updating a few key links in the `claude/` source files (acceptable since they're living docs)

---

## Appendix: Files to Create / Modify

### New Files

| File | Purpose |
|------|---------|
| `doc/guides/toc.yml` | Navigation for guides section |
| `doc/guides/getting-started.md` | New tutorial content |
| `doc/internals/toc.yml` | Navigation for internals section |
| `doc/internals/adr/toc.yml` | ADR navigation (lists all 32) |
| `doc/benchmarks/toc.yml` | Benchmarks section navigation (references `/benchmark` content) |

### Modified Files

| File | Changes |
|------|---------|
| `doc/docfx.json` | Add `claude/` content mappings, resource mappings, update `includePrivateMembers` |
| `doc/toc.yml` | Update top-level nav (Overview, Guides, API, Internals, Benchmarks) |
| `doc/index.md` | Redesign landing page |
| `doc/overview/toc.yml` | Rewrite to map to `claude/overview/` files |
| `doc/template/styles/main.css` | Add landing page styles, minor refinements |
| `.github/workflows/build-documentation.yml` | Update triggers, branch, actions versions |
| `.github/workflows/benchmark.yml` | **Delete** (replaced by local `/benchmark` skill) |

### Unchanged (Referenced Directly)

All `claude/overview/`, `claude/adr/`, `claude/reference/`, `claude/design/`, and `claude/assets/*.svg` files are included as-is via DocFX content mappings.

---

*This document is a design specification. Implementation follows the [Migration Plan](#migration-plan) phases.*
