# Documentation Tooling for Typhon

**Status:** Idea / Research
**Created:** 2026-01-26
**Category:** Infrastructure / Developer Experience

## Summary

This document evaluates documentation generation solutions for Typhon, a real-time ACID database engine. The goal is to produce an online documentation site that combines:

1. **API Reference** — generated from C# XML documentation comments (DocXML)
2. **Narrative / Conceptual Docs** — authored in Markdown (architecture guides, tutorials, how-to articles)

The evaluation surveys 50+ popular .NET OSS projects to identify what tools they use, then performs a deep feature comparison of the most viable options, with specific attention to Typhon's codebase characteristics.

---

## Table of Contents

- [Part 1: Industry Survey](#part-1-industry-survey)
- [Part 2: Tool Deep Dives](#part-2-tool-deep-dives)
- [Part 3: Comparison Matrix](#part-3-comparison-matrix)
- [Part 4: Hybrid Approaches](#part-4-hybrid-approaches)
- [Part 5: Typhon-Specific Analysis](#part-5-typhon-specific-analysis)
- [Part 6: Recommendations](#part-6-recommendations)

---

## Part 1: Industry Survey

Survey of 50+ popular .NET OSS projects and their documentation solutions.

### DocFX (~15-20 projects)

The most common choice for .NET-native API documentation.

| Project | Documentation URL | Notes |
|---------|-------------------|-------|
| .NET Runtime / BCL | docs.microsoft.com (API browser) | DocFX-generated API reference |
| Uno Platform | platform.uno/docs | Full DocFX site |
| BenchmarkDotNet | benchmarkdotnet.org | API + articles |
| Polly | pollydocs.org | Resilience library |
| Refit | reactiveui.github.io/refit | REST client |
| ReactiveUI | reactiveui.net/api | API explorer portion |
| DynamicData | reactiveui.net/api/dynamicdata | Part of ReactiveUI docs |
| OpenSettings | docs.opensettings.net | Configuration library |
| Unity packages | docs.unity3d.com | DocFX-based package docs |
| Various Microsoft SDKs | Various | Internal tooling standard |

### Microsoft Learn (~10-15 projects)

Official Microsoft documentation platform (not available to OSS projects outside Microsoft).

| Project | Notes |
|---------|-------|
| ASP.NET Core | Full framework docs |
| Entity Framework Core | ORM documentation |
| Roslyn | Compiler platform |
| SignalR | Real-time communication |
| gRPC .NET | RPC framework |
| Blazor | Web UI framework |
| MAUI | Cross-platform UI |
| Windows Community Toolkit | UI controls |
| MVVM Community Toolkit | MVVM patterns |

### Docusaurus (Verified .NET Projects)

Limited but growing adoption among .NET projects. Notably, adoption is much lower than in the JavaScript/Python ecosystems — most .NET projects that look modern actually use VitePress or MkDocs Material rather than Docusaurus.

| Project | Documentation URL | API Docs? | Notes |
|---------|-------------------|-----------|-------|
| RestSharp | restsharp.dev | Yes | HTTP client library; verified Docusaurus via page source (`__docusaurus_skipToContent_fallback`) |
| Stryker.NET | stryker-mutator.io/docs/stryker-net/ | No (narrative) | Mutation testing framework; Docusaurus with narrative-only docs |
| Discord.NET (DFMG example) | discord-net-dfmg.jan0660.dev | Yes (auto-generated) | Proof-of-concept using DocFxMarkdownGen bridge; not official Discord.NET docs |

**Bridge tooling for Docusaurus + .NET:**
- **DocFxMarkdownGen (DFMG)** — Primary bridge tool; converts DocFX YAML metadata to Docusaurus-compatible Markdown. Template repos: `Jan0660/dfmg-template`, `Jan0660/dfmg-template2` (with versioning + CI/CD)
- **AspNetCore.Docusaurus** — NuGet package for ASP.NET Core integration (less mature)

**Projects verified as NOT using Docusaurus** (despite modern-looking sites):
- Marten (martendb.io) → VitePress
- Wolverine (wolverinefx.net) → VitePress
- Testcontainers .NET (dotnet.testcontainers.org) → MkDocs Material
- Flurl (flurl.dev) → MkDocs

### Custom / Vendor-Hosted (~20-25 projects)

Many established projects use bespoke or vendor solutions.

| Project | Documentation URL | Approach |
|---------|-------------------|----------|
| Serilog | serilog.net | Custom site |
| NLog | nlog-project.org | Custom site |
| FluentValidation | fluentvalidation.net | Custom with API docs |
| FluentAssertions | fluentassertions.com | Custom site |
| AutoMapper | automapper.org | Custom site |
| Newtonsoft.Json | newtonsoft.com/json | Commercial site |
| Duende IdentityServer | docs.duendesoftware.com | Vendor docs |
| RavenDB | ravendb.net/docs | Vendor database docs |
| Npgsql | npgsql.org | Custom database provider docs |
| ImageSharp | Six Labors site | Vendor docs |
| Stride (game engine) | stride3d.net | Vendor docs |
| MonoGame | docs.monogame.net | Vendor docs |
| Seq | datalust.co/docs | Vendor (Readme.io) |
| MudBlazor | mudblazor.com | Component library docs |
| Radzen Blazor | blazor.radzen.com | Component library docs |
| NUKE | nuke.build/docs | Build system docs |
| Cake | cakebuild.net | Build system docs |
| Hangfire | docs.hangfire.io | Job scheduler docs |

### Statiq / Statiq.Docs (Verified .NET Projects)

Statiq is a .NET-native SSG (successor to Wyam). Most adoption is for blogs, but Spectre.Console stands out as a real library documentation site with API reference.

| Project | Documentation URL | API Docs? | Notes |
|---------|-------------------|-----------|-------|
| Spectre.Console | spectreconsole.net | Yes (spectreconsole.net/api/) | Most prominent Statiq.Docs user; full API reference from assemblies |
| .NET Foundation Website | dotnetfoundation.org | No | Organization website built with Statiq.Web |
| Statiq.dev | statiq.dev | Yes | Self-referential: Statiq's own docs built with Statiq |

**Additional Statiq.Web users** (blogs, not library docs):
- Dave Glick's blog (daveaglick.com) — Statiq creator, uses CleanBlog theme
- TechWatching (techwatching.dev) — Migrated from Wyam to Statiq
- Feiko.io — IoT/.NET blog with Statiq.Web + GitHub Pages
- Multiple developer blogs using the CleanBlog theme

**Key observation:** Spectre.Console is essentially the **only** significant .NET library using Statiq.Docs for full API documentation in production. Most other Statiq users are personal blogs using Statiq.Web (without API doc generation). This contrasts with DocFX which has dozens of library documentation sites.

### ReadTheDocs / Sphinx (~3-5 projects)

| Project | Documentation URL | Notes |
|---------|-------------------|-------|
| ArchUnitNET | archunitnet.readthedocs.io | Architecture testing |
| Ocelot | ocelot.readthedocs.io | API Gateway |
| Quartz.NET | — | Job scheduling |

### GitHub Wiki / README-Only (~10-15 projects)

Smaller or simpler projects relying on GitHub alone.

| Project | Notes |
|---------|-------|
| MediatR | README-based |
| Moq | README + wiki |
| Dapper | learndapper.com (tutorial site) |
| CsvHelper | GitHub Pages |
| dotnet-script | README |
| NBench | GitHub repository |
| Terminal.Gui | GitHub-based |
| NetArchTest | GitHub repository |

### Key Takeaway

**DocFX is the de facto standard** for .NET API documentation, but many projects supplement or replace it with modern SSGs for narrative content. There is a clear trend toward **Docusaurus** for new projects wanting better developer experience, often using a bridge tool to import API docs.

---

## Part 2: Tool Deep Dives

### 1. DocFX (Microsoft)

**Website:** https://dotnet.github.io/docfx
**GitHub:** ~4K stars, actively maintained (.NET Foundation project)
**Latest:** v2.78.x stable; v3 in development (timeline unclear)

#### How It Works

DocFX uses Roslyn to parse C# source code and extract XML documentation into YAML metadata files. These are then combined with Markdown conceptual articles and rendered into a static HTML site.

```
C# Source + XML Comments
        ↓ (Roslyn extraction)
    YAML Metadata
        ↓ (template rendering)
  Markdown Articles + YAML API
        ↓
    Static HTML Site
```

#### Strengths

- **Native DocXML support** — First-class C# XML comment parsing via Roslyn
- **Microsoft standard** — Same tool that powers docs.microsoft.com API browser
- **Cross-references** — Seamless linking between API docs and conceptual articles using `xref:` syntax
- **Extensible templates** — Customizable Mustache/Liquid templates
- **GitHub Pages hosting** — Well-supported via `docfx-action` GitHub Action
- **Multi-language** — Supports C#, VB.NET, F#, and REST APIs (via Swagger)

#### Weaknesses

- **Performance at scale** — Large codebases (20K+ YAML files) can take ~2 hours to build; memory usage can exceed 20GB
- **Versioning is complex** — Requires separate `docfx.json` per version; no built-in version selector UI
- **Live preview is basic** — `docfx --serve` works but isn't a hot-reload experience
- **Theming is dated** — Default templates look functional but not modern compared to Docusaurus/MkDocs Material
- **v3 uncertainty** — Community has been waiting for v3 since early 2020s

#### .NET Feature Support

| Feature | Support |
|---------|---------|
| Generics (`T`, constraints) | ✅ Full |
| Nullable reference types | ✅ Full |
| Extension methods | ✅ Full |
| Custom attributes | ✅ Full |
| `unsafe` / pointers | ✅ Full (Roslyn-based) |
| Cross-assembly refs | ✅ Full |
| Operators | ✅ Full |
| Records / init-only | ✅ Full |

---

### 2. Docusaurus v3.9 (Meta)

**Website:** https://docusaurus.io
**GitHub:** ~60K stars, very actively maintained
**Latest:** v3.9 (October 2025) — introduced AI-powered search

#### How It Works

Docusaurus is a React-based static site generator optimized for documentation. It does **not** natively handle C# API docs — a converter tool must generate Markdown from DocXML first.

```
C# Source + XML Comments
        ↓ (xmldoc2md, DefaultDocumentation, or DocFxMarkdownGen)
    Markdown Files (API reference)
        ↓
  Markdown Articles + API Markdown
        ↓ (Docusaurus build)
    React Static HTML Site
```

#### Strengths

- **Best-in-class versioning** — Built-in version dropdown, snapshot-based versioning
- **Modern UX** — Beautiful default theme, React-based, fully responsive
- **AI-powered search** — New in v3.9 (Oct 2025); also supports Algolia DocSearch
- **Massive ecosystem** — 60K stars, huge plugin/theme community
- **Hot reload** — Excellent authoring experience with fast feedback loop
- **MDX support** — Interactive React components embedded in Markdown

#### Weaknesses

- **No native C# API support** — Requires separate DocXML → Markdown conversion step
- **Conversion loses metadata** — Converted API docs may lose rich type information, cross-references, and .NET-specific constructs
- **Node.js dependency** — Requires Node.js 20+ (not .NET-native)
- **Two-step pipeline** — More complex CI/CD (convert then build)

#### .NET Feature Support (via converters)

| Feature | Support |
|---------|---------|
| Generics | ⚠️ Depends on converter quality |
| Nullable reference types | ⚠️ Often lost in conversion |
| Extension methods | ⚠️ May not be grouped correctly |
| Custom attributes | ❌ Usually lost |
| `unsafe` / pointers | ⚠️ Depends on converter |
| Cross-assembly refs | ❌ Links break across assemblies |
| Operators | ⚠️ Inconsistent |

---

### 3. Statiq.Docs

**Website:** https://www.statiq.dev
**GitHub:** Statiq.Framework + Statiq.Web + Statiq.Docs
**Latest:** Statiq.Docs v1.0.0-beta.17 (actively developed)

#### How It Works

Statiq is a .NET-native static site generator (successor to Wyam). Statiq.Docs is a specialized module that uses Roslyn to extract API documentation, similar to DocFX but with a fully programmable pipeline.

```
C# Source + XML Comments
        ↓ (Roslyn via Statiq.Docs)
    In-Memory Documents
        ↓ (Statiq.Web pipeline)
  Razor Templates + Markdown
        ↓
    Static HTML Site
```

#### Strengths

- **Fully .NET-native** — Written in C#, extensible via C# code
- **Maximum customization** — Code-driven pipeline, not just configuration
- **Roslyn-based** — Same quality of C# parsing as DocFX
- **Razor templates** — Full ASP.NET Core Razor support
- **Incremental builds** — Supported natively
- **Modern architecture** — ASP.NET Core-based, clean design

#### Weaknesses

- **Small community** — Much less adoption than DocFX or Docusaurus
- **Still in beta** — Statiq.Docs hasn't reached 1.0 stable yet
- **Limited documentation** — Ironic for a documentation tool
- **Fewer themes** — Less visual polish out of the box
- **Learning curve** — Requires understanding Statiq's pipeline/module architecture

#### .NET Feature Support

| Feature | Support |
|---------|---------|
| Generics | ✅ Full (Roslyn) |
| Nullable reference types | ✅ Full |
| Extension methods | ✅ Full |
| Custom attributes | ✅ Full |
| `unsafe` / pointers | ✅ Full |
| Cross-assembly refs | ✅ Full |
| Operators | ✅ Full |

---

### 4. Sandcastle Help File Builder (SHFB)

**Website:** https://github.com/EWSoftware/SHFB
**GitHub:** ~2.2K stars, actively maintained
**Latest:** v2025.12.18.0 (December 2025)

#### How It Works

SHFB processes compiled assemblies (DLLs) and their accompanying XML documentation files to produce documentation in multiple output formats (HTML website, CHM, Word, PDF).

```
Compiled DLL + XML Doc File
        ↓ (Reflection + XML parsing)
    Documentation Model
        ↓ (Presentation styles)
  HTML / CHM / PDF / Word
```

#### Strengths

- **Gold standard for .NET API docs** — Most complete API documentation of any tool
- **NamespaceDoc support** — Document entire namespaces with dedicated content
- **Multiple output formats** — HTML website, CHM, Word, PDF
- **Visual Studio integration** — GUI-based project configuration
- **Markdown support** — Added in 2025 release (was previously MAML-only for conceptual docs)
- **Still actively maintained** — Regular releases through 2025

#### Weaknesses

- **Dated architecture** — Originally designed for offline help formats
- **Web output less modern** — Generated websites look functional but dated
- **Heavier build process** — Processes compiled binaries, not source
- **Less CI/CD friendly** — Historically GUI-focused, though MSBuild support exists
- **Migration overhead** — Older projects may use MAML instead of Markdown

#### .NET Feature Support

| Feature | Support |
|---------|---------|
| Generics | ✅ Full |
| Nullable reference types | ✅ Full |
| Extension methods | ✅ Full |
| Custom attributes | ✅ Full |
| `unsafe` / pointers | ✅ Full |
| Cross-assembly refs | ✅ Excellent |
| Operators | ✅ Full |
| NamespaceDoc | ✅ Unique feature |

---

### 5. MkDocs + Material Theme

**Website:** https://squidfunk.github.io/mkdocs-material
**GitHub:** ~20K stars (Material theme), actively maintained
**Status:** Disco search engine replacing old library (2025-2026)

#### How It Works

MkDocs is a Python-based SSG for documentation. It generates sites from Markdown files with YAML-based navigation. Has no native C# support.

```
Markdown Files + mkdocs.yml
        ↓ (MkDocs build)
    Static HTML Site (Material theme)
```

#### Strengths

- **Beautiful Material Design** — Best-looking documentation theme in the ecosystem
- **Excellent Markdown extensions** — Admonitions, tabs, annotations, code copy buttons
- **Versioning via `mike`** — Multi-version deployment tool
- **Very fast builds** — Python-based but optimized
- **Live reload** — `mkdocs serve` with hot reload
- **mkdocstrings** — API doc plugin (but no C# handler)

#### Weaknesses

- **No C# API support** — mkdocstrings supports Python, JS, TS, C, but NOT C#
- **Requires external conversion** — Must use xmldoc2md or similar to generate API docs
- **Python dependency** — Not .NET-native; requires Python toolchain
- **Versioning via external tool** — `mike` is separate from MkDocs

#### .NET Feature Support

No native .NET support. Entirely dependent on converter tool quality.

---

### 6. DocXML-to-Markdown Converters

These are not documentation sites themselves but bridge tools that convert C# XML documentation to Markdown for use with any SSG.

#### xmldoc2md (charlesdevandiere)

- **GitHub:** ~50 stars
- **Status:** ⚠️ Last commit July 2024 — appears unmaintained
- **Input:** Assembly DLL + XML doc file
- **Output:** Markdown files (one per type)
- **Quality:** Basic; loses some metadata

#### XmlDocMarkdown (ejball)

- **Website:** ejball.com/XmlDocMarkdown
- **Status:** More actively maintained than xmldoc2md
- **Input:** Assembly DLL + XML doc file
- **Output:** Markdown or HTML
- **Quality:** Better than xmldoc2md; handles more constructs

#### DefaultDocumentation (Doraku)

- **GitHub:** Active project
- **Status:** ✅ Actively maintained
- **Input:** Assembly + XML doc file
- **Output:** Markdown files
- **Quality:** Good; plugin system for customization
- **Unique:** MSBuild integration, configurable access modifier filtering

#### DocFxMarkdownGen (Jan0660)

- **Purpose:** Converts DocFX YAML metadata to Docusaurus-compatible Markdown
- **Status:** Specialized bridge tool
- **Use case:** DocFX extraction → Docusaurus rendering

---

### 7. Other Tools (Brief Coverage)

#### Doxygen

- **GitHub:** ~20K stars, actively maintained
- **C# support:** Limited — doesn't recognize `inheritdoc`, has issues with `<para>`, `<code>` tags, enum documentation, and C# 9+ features (records)
- **Verdict:** Not recommended for .NET-primary projects

#### Hugo

- **GitHub:** ~70K stars
- **C# support:** None — general-purpose SSG
- **Verdict:** Excellent for blogs/landing pages, not for API documentation

#### Sphinx

- **GitHub:** ~8K stars
- **C# support:** sphinxcontrib-dotnetdomain exists but is incomplete (WIP)
- **Verdict:** Great for Python docs, not suitable for .NET without major custom work

---

## Part 3: Comparison Matrix

### Feature Comparison

| Feature | DocFX | Docusaurus | Statiq.Docs | SHFB | MkDocs Material |
|---------|-------|------------|-------------|------|-----------------|
| **DocXML API generation** | ✅ Native | ❌ Needs converter | ✅ Native | ✅ Native | ❌ Needs converter |
| **Markdown narrative docs** | ✅ Good | ✅ Excellent | ✅ Good | ✅ New (2025) | ✅ Excellent |
| **Search** | ✅ Client-side | ✅ AI-powered (v3.9) | ✅ Built-in | ✅ Full | ✅ Client-side (Disco coming) |
| **Versioning** | ⚠️ Complex setup | ✅ Best-in-class | ✅ Supported | ✅ Manual | ✅ Via `mike` |
| **GitHub Pages** | ✅ Via docfx-action | ✅ Native | ✅ Supported | ⚠️ Possible | ✅ Via `mike` |
| **Hot reload / preview** | ⚠️ Basic | ✅ Excellent | ✅ Good | ✅ VS integration | ✅ Excellent |
| **Build performance** | ⚠️ Slow at scale | ✅ Fast | ✅ Good | ✅ Good | ✅ Very fast |
| **Modern UX / themes** | ⚠️ Functional | ✅ Beautiful | ⚠️ Limited themes | ⚠️ Dated | ✅ Beautiful |
| **CI/CD integration** | ✅ Good | ✅ Excellent | ✅ Good | ⚠️ Moderate | ✅ Good |
| **Plugin ecosystem** | ⚠️ Moderate | ✅ Large | ⚠️ Small | ✅ Good | ✅ Large |

### .NET-Specific Feature Support

| Feature | DocFX | Docusaurus* | Statiq.Docs | SHFB | MkDocs* |
|---------|-------|-------------|-------------|------|---------|
| **C# generics** | ✅ | ⚠️ | ✅ | ✅ | ⚠️ |
| **Nullable refs** | ✅ | ⚠️ | ✅ | ✅ | ⚠️ |
| **Extension methods** | ✅ | ⚠️ | ✅ | ✅ | ⚠️ |
| **Custom attributes** | ✅ | ❌ | ✅ | ✅ | ❌ |
| **`unsafe` / pointers** | ✅ | ⚠️ | ✅ | ✅ | ⚠️ |
| **Cross-assembly refs** | ✅ | ❌ | ✅ | ✅ | ❌ |
| **Operators** | ✅ | ⚠️ | ✅ | ✅ | ⚠️ |
| **Records / init** | ✅ | ⚠️ | ✅ | ✅ | ⚠️ |

*\* Via converter tools — quality depends on converter used*

### Project Health

| Metric | DocFX | Docusaurus | Statiq.Docs | SHFB | MkDocs Material |
|--------|-------|------------|-------------|------|-----------------|
| **GitHub stars** | ~4K | ~60K | ~1K (framework) | ~2.2K | ~20K |
| **Backing** | Microsoft / .NET Foundation | Meta | Independent | Independent | Independent (sponsor-funded) |
| **Last release** | 2025 (active) | Oct 2025 (v3.9) | Beta (active) | Dec 2025 | 2025 (active) |
| **Maturity** | Stable | Stable | Beta | Stable | Stable |
| **Learning curve** | Steep | Gentle | Steep | Medium | Very gentle |
| **Language** | C# | JavaScript/React | C# | C# | Python |

---

## Part 4: Hybrid Approaches

The most promising pattern emerging in the .NET ecosystem is a **hybrid approach** that uses a .NET-native tool for API extraction and a modern SSG for rendering.

### Pattern A: DocFX Extraction → Docusaurus Rendering

```
C# Source
  ↓ (DocFX metadata command)
YAML API Metadata
  ↓ (DocFxMarkdownGen)
Markdown API Files
  ↓ (combined with hand-written articles)
Docusaurus Build → Beautiful Static Site
```

**Pros:** Best of both worlds — Roslyn-quality API extraction + modern UX
**Cons:** Three-tool pipeline; cross-references may break; extra maintenance

### Pattern B: DefaultDocumentation → Any SSG

```
Compiled DLL + XML
  ↓ (DefaultDocumentation MSBuild task)
Markdown API Files
  ↓ (combined with hand-written articles)
Docusaurus / MkDocs / Hugo → Static Site
```

**Pros:** Simpler pipeline (MSBuild-integrated); works with any SSG
**Cons:** Assembly-based (not source-based); may lose some source-level detail

### Pattern C: DocFX for API + Separate Narrative Site

```
                    ┌─ DocFX → API Reference Site (/api)
C# Source ─────────┤
Hand-written MD ───┘─ Docusaurus → Narrative Site (/docs)
```

**Pros:** Each tool does what it's best at; no converter compromises
**Cons:** Two separate sites to maintain and deploy; navigation feels disconnected

### Pattern D: Pure DocFX with Custom Theme

```
C# Source + Markdown Articles
  ↓ (DocFX with modern template)
Single Unified Site
```

**Pros:** Simplest pipeline; one tool; native cross-references work perfectly
**Cons:** Theme customization is harder; UX won't match Docusaurus/MkDocs quality

---

## Part 5: Typhon-Specific Analysis

### Codebase Characteristics

Typhon has specific properties that affect documentation tooling choice:

1. **Heavy `unsafe` code** — Pointers (`byte*`, `T*`), `stackalloc`, `GCHandle`, `Unsafe.As<T>()`. API docs must render pointer types correctly.

2. **Complex generic types** — `ChunkRandomAccessor<T>`, `ComponentTable<T>`, `BPTreeNode<TKey, TValue>`. Nested generics with constraints (`where T : unmanaged`) must be visible.

3. **Blittable struct requirements** — Components marked with `[Component]`, `[Field]`, `[Index]` attributes. These attributes convey critical semantic meaning and should appear in API docs.

4. **Internal cross-assembly references** — Single assembly (`Typhon.Engine`) but many cross-referencing types. B+Tree implementations reference `ChunkBasedSegment` which references `PagedMMF`.

5. **Performance-critical API surface** — Users need to understand memory layout, cache behavior, and allocation patterns from the docs. Code examples with `ref`, `in`, `Span<T>` must render correctly.

6. **Existing documentation in `claude/`** — ~50 Markdown files of architecture docs, ADRs, design docs, and research. These should integrate seamlessly as narrative content.

### Tool Suitability for Typhon

| Typhon Need | DocFX | Docusaurus+Conv | Statiq.Docs | SHFB |
|-------------|-------|-----------------|-------------|------|
| `unsafe` pointer types | ✅ Renders correctly | ⚠️ Converter may strip | ✅ Renders correctly | ✅ Renders correctly |
| Generic constraints | ✅ Shows `where T : unmanaged` | ⚠️ May lose constraints | ✅ Full rendering | ✅ Full rendering |
| `[Component]` attributes | ✅ Shown in metadata | ❌ Lost in conversion | ✅ Shown in metadata | ✅ Shown + NamespaceDoc |
| Cross-type linking | ✅ `xref:` links work | ⚠️ Links may break | ✅ Full linking | ✅ Full linking |
| `ref`/`in`/`Span<T>` params | ✅ Full rendering | ⚠️ Depends on converter | ✅ Full rendering | ✅ Full rendering |
| Existing Markdown docs | ✅ Native inclusion | ✅ Excellent | ✅ Native inclusion | ✅ Supported (2025+) |
| GitHub Pages hosting | ✅ Easy | ✅ Easy | ✅ Easy | ⚠️ Extra setup |
| CI/CD (GitHub Actions) | ✅ `docfx-action` | ✅ Native | ✅ `dotnet run` | ⚠️ MSBuild |

### Risk Assessment

| Tool | Risk Level | Primary Risk |
|------|-----------|--------------|
| **DocFX** | Low | Performance if codebase grows very large |
| **Docusaurus + Converter** | Medium-High | Converter doesn't handle `unsafe`/attributes/constraints correctly |
| **Statiq.Docs** | Medium | Beta status; small community; limited docs about the docs tool itself |
| **SHFB** | Low-Medium | Web output looks dated; less CI/CD friendly |

---

## Part 6: Recommendations

### Primary Recommendation: DocFX (Pattern D)

**For Typhon specifically, DocFX is the strongest choice** despite its UX limitations, because:

1. **Zero conversion loss** — All of Typhon's advanced C# features (`unsafe`, generics with constraints, custom attributes, pointer types) are rendered correctly since DocFX uses Roslyn directly.

2. **Single pipeline** — No converter step means no broken cross-references, no lost metadata, no extra tools to maintain.

3. **Microsoft standard** — The same tool that documents the .NET runtime itself. If it can handle `System.Runtime.InteropServices`, it can handle `Typhon.Engine`.

4. **Already in use** — Typhon already has a working DocFX setup in `doc/docfx.json` with the `modern` template, search enabled, and API metadata extraction configured. The existing config includes `includePrivateMembers: true` and a filter config. This is not a greenfield decision — it's about evaluating whether to continue with DocFX or migrate.

5. **Markdown integration** — The ~50 Markdown files in `claude/` can be restructured as DocFX conceptual articles with minimal changes.

6. **Performance is manageable** — Typhon is a single assembly with moderate API surface. DocFX performance issues primarily affect very large multi-assembly solutions (20K+ YAML files). Typhon should be well within acceptable build times.

#### Recommended Setup

```
doc/
├── docfx.json              # DocFX configuration
├── index.md                # Landing page
├── articles/               # Narrative content (from claude/ docs)
│   ├── architecture/       # Overview series
│   ├── guides/             # How-to articles
│   └── adr/                # Architecture Decision Records
├── api/                    # Auto-generated API reference
│   └── .gitignore          # Don't commit generated files
└── templates/              # Custom theme (optional)
```

### Alternative: Docusaurus + DefaultDocumentation (Pattern B)

If the modern UX of Docusaurus is a priority over API doc fidelity, this hybrid approach is the best alternative:

1. Use **DefaultDocumentation** (MSBuild-integrated, actively maintained) to generate Markdown API docs from compiled assemblies.
2. Combine generated API Markdown with hand-written narrative Markdown.
3. Build with **Docusaurus** for versioning, search, and beautiful output.

**Trade-off:** Some loss of .NET-specific metadata (attributes, generic constraints, `unsafe` annotations) in exchange for significantly better UX, versioning, and search capabilities.

### Future-Proof Consideration: Statiq.Docs

If Statiq.Docs reaches 1.0 stable with improved documentation and themes, it would be the ideal long-term choice for a .NET project like Typhon: fully .NET-native, Roslyn-based API extraction, maximum customization via C# code, and modern web output. Worth monitoring but not ready for production use today.

### Not Recommended for Typhon

- **MkDocs Material** — No C# handler in mkdocstrings; beautiful but wrong tool for API-heavy .NET docs
- **Hugo** — No API doc support at all
- **Sphinx** — C# domain extension is incomplete
- **Doxygen** — Known issues with modern C# constructs
- **SHFB** — While technically excellent, the dated web output and heavier workflow don't align with a modern open-source project

---

## Decision Matrix

| Criteria | Weight | DocFX | Docusaurus+DD | Statiq.Docs |
|----------|--------|-------|---------------|-------------|
| API doc quality | 30% | 10 | 6 | 10 |
| Modern UX | 15% | 5 | 10 | 6 |
| Pipeline simplicity | 15% | 9 | 5 | 7 |
| .NET feature coverage | 20% | 10 | 5 | 10 |
| Community / support | 10% | 7 | 10 | 3 |
| Versioning | 10% | 5 | 10 | 7 |
| **Weighted Score** | — | **8.15** | **6.85** | **7.65** |

---

## Current State: Existing DocFX Setup

Typhon already has a working DocFX configuration at `doc/docfx.json`:

- **Template:** `default` + `modern` + custom `template/` overrides
- **API extraction:** All `.csproj` files under `src/`, including private members
- **Filter:** `filterConfig.yml` for controlling which APIs to document
- **Search:** Enabled (`_enableSearch: true`)
- **Output:** `doc/live/` directory with generated static site
- **Branding:** Custom logo (`typhon.svg`), favicon, app name, footer

The existing site is already deployed at https://nockawa.github.io/Typhon/

## Next Steps

Since DocFX is already in place, the question becomes whether to:

1. **Stay with DocFX (recommended)** — Improve the existing setup with better narrative content, custom theme refinements, and CI/CD automation
2. **Migrate to alternative** — Only if DocFX's limitations (versioning, UX) become blocking
3. **Hybrid approach** — Keep DocFX for API docs, add a modern narrative site alongside

If staying with DocFX:

1. **Improve narrative content** — Migrate selected `claude/` Markdown files into `doc/articles/`
2. **Refine the modern template** — Customize the `template/` overrides for better visual polish
3. **Add CI/CD** — Configure GitHub Actions to auto-build and deploy docs on push to main
4. **Evaluate versioning needs** — Determine if multi-version docs are needed as Typhon matures
5. **Review filter config** — Ensure `filterConfig.yml` exposes the right public API surface

---

*This document is a research artifact. No decisions have been made. See the recommendation section for analysis.*
