# Typhon.Shell — Interactive Database Shell

An interactive REPL and diagnostic shell for the Typhon embedded database engine.

> **TL;DR:** `Typhon.Shell` (executable: `tsh`) is a console application that hosts a `DatabaseEngine` in-process, providing interactive CRUD, engine diagnostics, administration, and batch scripting. It is the single process that owns the database file — not a client to a server.

## Overview

Typhon is an embedded database — it runs in-process with the application. There is no server, no wire protocol, no separate client. This means there is currently no standalone tool to interact with a database: you must write C# code, compile it, and run it.

`Typhon.Shell` fills this gap by being a self-contained console application that hosts the engine directly. It serves three audiences:

1. **Developers** — experiment with the engine, prototype schemas, explore data interactively
2. **Operators** — inspect database health, run diagnostics, perform maintenance (backup, integrity checks)
3. **CI/CD pipelines** — scriptable batch operations for automated testing, migrations, and validation

## Target Audience

- Typhon engine developers (primary — this is a dev tool first)
- Application developers building on Typhon
- CI/CD systems needing scripted database operations

## Document Series

| Part | Title | Focus |
|------|-------|-------|
| [01](./01-core-concepts.md) | **Core Concepts** | Architecture, process model, session lifecycle |
| [02](./02-schema-and-data.md) | **Schema & Data Operations** | Component loading, transaction CRUD, query |
| [03](./03-diagnostics.md) | **Diagnostics & Inspection** | Engine internals visibility, performance insight |
| [04](./04-admin-and-scripting.md) | **Administration & Scripting** | Maintenance operations, batch mode, CI/CD integration |

## Key Design Principles

### Single-Process Ownership

The shell *is* the database process. Only one process can open a database file at a time (Typhon's concurrency model is in-process). The shell opens the file, owns it exclusively, and releases it on close. This is identical to how `sqlite3` works.

### Schema = Code (The Bridge Problem)

Typhon components are compiled C# structs. The user creates a class library with their component definitions, compiles it, and loads the resulting DLL into `tsh`. The shell discovers `[Component]` types via reflection and builds a field map for runtime text-to-binary conversion. Built-in test components are also available for immediate experimentation.

The text-to-struct converter supports multiple input formats for different contexts: **brace format** for interactive use (`{ Health=95.0 }`), **compact format** for script bulk data, and **JSON format** for CI/CD pipes. All formats feed the same conversion pipeline. See [Part 02](./02-schema-and-data.md) for the full type table and format details.

### Diagnostic-First

While CRUD is useful, the killer feature is **engine visibility**. No other tool can show you page cache hit rates, B+Tree depth, revision chain lengths, segment occupancy, or MVCC state. The shell makes Typhon's internals observable interactively.

### Scriptable

Every interactive command can also be run from a file or piped via stdin. This makes the shell useful for CI/CD, automated testing, and repeatable scenarios.

**Two scripting tiers:**
- **`.tsh` line-oriented format (v1)** — One command per line, `@compact`/`@json` data blocks. No variables, no control flow. Simple scripts stay simple; complex automation uses the host shell.
- **Scriban-powered scripts (post-v1)** — Files with a `#!scriban` header get variables, loops, conditionals, and expressions via [Scriban](https://github.com/scriban/scriban). Zero-dependency upgrade path for scripts that outgrow line-oriented format but don't warrant a bash wrapper.

See [Part 04](./04-admin-and-scripting.md) for the full script format specification and advanced scripting design.

## Technology Stack

| Library | Role |
|---------|------|
| **[Spectre.Console](https://spectreconsole.net/)** | Rich terminal output: tables, colors, progress bars, tree views. Powers all diagnostic and query output formatting. |
| **[Spectre.Console.Cli](https://spectreconsole.net/cli/)** | Command-line argument parsing and command routing. Defines the `tsh` CLI surface (`--exec`, `-c`, `--format`, etc.) and dispatches to command handlers. |
| **[PrettyPrompt](https://github.com/waf/PrettyPrompt)** | REPL line editing: syntax highlighting, autocompletion with tooltips, persistent history, multi-line input, cross-platform clipboard. |
| **[Scriban](https://github.com/scriban/scriban)** *(post-v1)* | Advanced script engine: variables, loops, conditionals, expressions. Enables `#!scriban` mode for `.tsh` files that outgrow line-oriented format. |
| **[Terminal.Gui](https://github.com/gui-cs/Terminal.Gui)** | Full-screen TUI for interactive navigation sessions. Powers the resource tree explorer (`resources` command) with keyboard-driven drill-down, split-pane layout, and live detail view. |

**Why Spectre?**
- **Spectre.Console** is the de facto standard for rich .NET console output. It handles terminal capability detection, Unicode/emoji fallback, and color support automatically — things that are tedious and error-prone to build from scratch.
- **Spectre.Console.Cli** (formerly Spectre.CLI) provides a type-safe command/argument model with built-in help generation, validation, and error reporting. It replaces both `System.CommandLine` (which has been in preview for years) and hand-rolled argument parsing.
- Using one library family for both CLI parsing and output formatting keeps dependencies minimal and ensures visual consistency.

**Why PrettyPrompt?**
- Extracted from [CSharpRepl](https://github.com/waf/CSharpRepl) (3.3K ⭐) as a standalone library — battle-tested in a real, widely-used REPL.
- Provides syntax highlighting, autocompletion menus with documentation tooltips, persistent history with filtering, multi-line input with word-wrap, and cross-platform copy/paste — all the hard terminal problems solved.
- Customization via `IPromptCallbacks` interface: `tsh` plugs in its own completion logic (component names, field names, commands) and syntax highlighting (keywords vs. values vs. strings) without forking or wrapping the library.
- Targets .NET 6+ (compatible through .NET 10). Single dependency: `TextCopy` (clipboard). License: MPL-2.0.
- Chosen over `ReadLine` (unmaintained since 2017), `ReadLine.Ext` (minimal community), and `InteractiveReadLine` (abandoned 2020). See [Part 04](./04-admin-and-scripting.md) for the full evaluation.

**Why Scriban? (post-v1)**
- Zero transitive dependencies, BSD-2-Clause, 3.8K ⭐, actively maintained (v6.5.2, Nov 2025).
- `ScriptOnly` mode strips template markers — scripts use plain `if`/`end`, `for`/`end` syntax that blends naturally with tsh command keywords.
- Built-in sandboxing via `TemplateContext` — expose tsh commands as callable functions while blocking filesystem/network access.
- Chosen over Roslyn Scripting (1.5s cold start, 15MB deps), NLua (native binaries break global tool), Lua-CSharp (Lua syntax alien to .NET devs), ExpressionEvaluator (maintenance-only). See [Part 04](./04-admin-and-scripting.md) for the full evaluation.

**Why Terminal.Gui?**
- Spectre.Console excels at formatted output and one-shot selection prompts, but it cannot do continuous interactive navigation. Its `Tree` widget is display-only, its `Live` display explicitly prohibits simultaneous keyboard input, and its prompt internals are private API.
- Terminal.Gui's `TreeView<T>` is purpose-built for keyboard-driven hierarchical navigation: arrow keys (up/down to move, right to expand, left to collapse), `SelectionChanged` event (to update a detail pane), lazy loading via `ITreeBuilder<T>`, filtering, and custom per-node colors.
- Terminal.Gui takes over the terminal via alternate screen buffer (the same mechanism used by `vim` and `less`). This means it cannot run simultaneously with Spectre.Console or PrettyPrompt, but it can run **sequentially** — the `resources` command launches a Terminal.Gui session, and when the user presses `q`, control returns to the REPL. The scrollback is preserved because the alternate screen buffer is independent.
- Evaluated alternatives: Consolonia (Avalonia-based TUI — too heavy, beta quality), custom implementation on raw `Console.ReadKey()` + Spectre renderables (several hundred lines of keyboard/scrolling/terminal-refresh code for something Terminal.Gui provides out of the box).

**Layer split:**
- **PrettyPrompt** handles *input* — the prompt line, editing, completions, highlighting.
- **Spectre.Console** handles *output* — tables, progress bars, diagnostic displays.
- **Spectre.Console.Cli** handles the *outer CLI* — `tsh --exec file.tsh`, `tsh -c "count CompA"`.
- **Scriban** handles *advanced scripts* — `#!scriban` files with variables, loops, expressions.
- **Terminal.Gui** handles *interactive sessions* — full-screen TUI for resource tree navigation, drill-down exploration.
- **Hand-rolled parser** handles *inner REPL commands* — `create PlayerStats { ... }`, `query ... where ...`.

These six layers don't overlap: each owns a distinct responsibility. The terminal is used sequentially — PrettyPrompt during input, Spectre.Console during output, Terminal.Gui during interactive sessions.

## Quick Reference — Proposed Command Categories

| Category | Examples | Purpose |
|----------|----------|---------|
| **Database** | `open`, `close`, `info` | File lifecycle |
| **Schema** | `load-schema`, `schema`, `describe` | Component type management |
| **Transaction** | `begin`, `commit`, `rollback` | Transaction control |
| **Data** | `create`, `read`, `update`, `delete` | Entity CRUD |
| **Query** | `query`, `count`, `find` | Data retrieval |
| **Diagnostics** | `cache-stats`, `segments`, `btree`, `revisions`, `transactions` | Engine internals |
| **Navigation** | `resources` | Interactive resource tree explorer (full-screen TUI) |
| **Admin** | `checkpoint`, `verify`, `compact`, `backup` | Maintenance |
| **Shell** | `help`, `history`, `set`, `exit` | Shell control |

## How to Use This Series

1. **Start with this README** for the big picture
2. **Read [Part 01](./01-core-concepts.md)** for architecture and session model
3. **Read [Part 02](./02-schema-and-data.md)** for the schema bridge problem and data operations
4. **Read [Part 03](./03-diagnostics.md)** for the diagnostic capabilities (the most unique value)
5. **Read [Part 04](./04-admin-and-scripting.md)** for admin and CI/CD usage

## Decisions (Cross-Cutting)

- [x] **CLI framework: Spectre.Console + Spectre.Console.Cli** — Spectre.Console for rich output (tables, colors, progress), Spectre.Console.Cli for CLI argument parsing and command routing. Replaces `System.CommandLine` (perpetual preview) and hand-rolled arg parsing.
- [x] **Output formats: table, full-table, JSON, CSV** — Powered by Spectre.Console's table/grid rendering for terminal output, with JSON and CSV formatters for pipe/CI usage.
- [x] **REPL command parsing: hand-rolled** — The inner REPL commands (`create`, `query`, `begin`, etc.) use a hand-rolled parser. Spectre.Console.Cli handles the outer CLI (`tsh --exec`, `tsh -c`), not the REPL loop.
- [x] **Script format: line-oriented + data block directives** — No variables, no control flow. See [Part 04](./04-admin-and-scripting.md).
- [x] **REPL line editing: [PrettyPrompt](https://github.com/waf/PrettyPrompt)** — Syntax highlighting, autocompletion, persistent history, multi-line input. Customized via `IPromptCallbacks` for `tsh`-specific completions and highlighting. Chosen over `ReadLine` (dead since 2017), `ReadLine.Ext` (5 ⭐), `InteractiveReadLine` (dead since 2020).

- [x] **First-class product in `src/`** — `Typhon.Shell` lives at `src/Typhon.Shell/`. Shipped as a NuGet global tool: `dotnet tool install -g typhon-shell` (gives `tsh` on PATH).
- [x] **Database creation via `open`** — `open mydb.typhon` creates the file if it doesn't exist (sqlite3 convention). No `--create` flag needed.
- [x] **No plugins/extensions** — `.tsh` scripts + Unix pipes + the C# API cover all extensibility needs. Scripts handle repeatable workflows, pipes handle tool composition, and anything requiring real logic belongs in C# using the engine API directly. A plugin system would add surface area, versioning complexity, and a discoverability problem for minimal benefit over these three existing escape hatches.
- [x] **Advanced scripting (post-v1): [Scriban](https://github.com/scriban/scriban)** — `#!scriban` header or `--script` flag enables variables, loops, conditionals, and expressions. Zero deps, BSD-2-Clause, 3.8K ⭐. Bridges the gap between simple `.tsh` scripts and full bash wrappers. See [Part 04](./04-admin-and-scripting.md).
- [x] **Interactive TUI: [Terminal.Gui](https://github.com/gui-cs/Terminal.Gui)** — Full-screen interactive sessions (resource tree explorer) using `TreeView<T>` with keyboard navigation, detail pane, and lazy loading. Runs sequentially with Spectre.Console/PrettyPrompt via alternate screen buffer. Spectre.Console's `Tree` widget is display-only and its `Live` display prohibits keyboard input — Terminal.Gui fills this gap. See [Part 03](./03-diagnostics.md).

## Open Questions (Cross-Cutting)

*(None remaining — all resolved.)*

## Related

- [ADR-004: Embedded Engine, No Server](../../adr/004-embedded-engine-no-server.md) — why there's no client-server protocol
- [Overview 03: Storage](../../overview/03-storage.md) — PagedMMF internals that diagnostics would expose
- [Overview 04: Data](../../overview/04-data.md) — MVCC/revision model that diagnostics would expose
- [Overview 08: Resources](../../overview/08-resources.md) — Resource graph that the `resources` navigator directly exposes
- [Overview 09: Observability](../../overview/09-observability.md) — existing telemetry (shell reads from the same Resource Graph, not through OTel)
