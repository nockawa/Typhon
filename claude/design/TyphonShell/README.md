# Typhon.Shell Design — Interactive Database Shell (tsh)

**Date:** February 2026
**Status:** Draft
**GitHub Issue:** [#65](https://github.com/nockawa/Typhon/issues/65)
**Prior Ideas:** [ideas/tooling/TyphonShell/](../../ideas/tooling/TyphonShell/README.md)
**Related ADRs:** [ADR-004: Embedded Engine](../../adr/004-embedded-engine-no-server.md)

---

> 💡 **TL;DR:** `tsh` is a single-process interactive shell for Typhon — think `sqlite3` for the ECS world. It hosts the engine in-process, provides CRUD, diagnostics, and scripting. Jump to [Phase Roadmap](#phase-roadmap) for the implementation plan, or directly to [Phase 1](./01-phase1-core.md) to start reading the spec.

---

## Table of Contents

1. [Overview](#overview)
2. [Target Audience](#target-audience)
3. [Technology Stack](#technology-stack)
4. [Phase Roadmap](#phase-roadmap)
5. [Project Structure](#project-structure)
6. [Cross-Cutting Design Decisions](#cross-cutting-design-decisions)
7. [Post-v1 Roadmap](#post-v1-roadmap)
8. [Related Documents](#related-documents)

## 1. Overview

Typhon is an embedded database — it runs in-process with the application. There is no server, no wire protocol, no separate client. This means there is currently no standalone tool to interact with a database: you must write C# code, compile it, and run it.

`Typhon.Shell` (executable: `tsh`) fills this gap. It is a console application that hosts a `DatabaseEngine` in-process, providing:

- **Interactive CRUD** — create, read, update, delete entities via a REPL
- **Engine diagnostics** — page cache, segments, B+Trees, MVCC state, resource graph
- **Batch scripting** — `.tsh` script files, pipe mode, single-command mode for CI/CD

The shell is the **single process that owns the database file** — not a client to a server. Only one process can open a database file at a time (Typhon's concurrency model is in-process). This is identical to how `sqlite3` works.

### Key Design Principles

| Principle | Description |
|-----------|-------------|
| **Single-process ownership** | The shell *is* the database process. It opens the file, owns it exclusively, and releases it on close. |
| **Schema = Code** | Components are compiled C# structs in a DLL. The shell loads the assembly, discovers `[Component]` types via reflection, and builds a field map for text-to-binary conversion. |
| **Diagnostic-first** | CRUD is table stakes. The killer feature is direct visibility into engine internals: page cache, B+Trees, revision chains, resource graph — no other tool can provide this. |
| **Scriptable** | Every interactive command can also be run from a file or piped via stdin. |

## 2. Target Audience

- **Typhon engine developers** (primary) — debug internals, prototype features, validate storage layout
- **Application developers** — experiment with schemas, explore data interactively
- **CI/CD systems** — scriptable batch operations for testing, validation, and maintenance

## 3. Technology Stack

### Package Reference

| Library | NuGet Package | Version | Phase | Role |
|---------|---------------|---------|-------|------|
| **Spectre.Console** | `Spectre.Console` | 0.54.0 | Phase 1 | Rich terminal output: tables, colors, markup |
| **Spectre.Console.Cli** | `Spectre.Console.Cli` | 0.53.1 | Phase 1 | CLI argument parsing and command routing |
| **PrettyPrompt** | `PrettyPrompt` | 4.1.1 | Phase 1 | REPL line editing: completions, highlighting, history |
| **Terminal.Gui** | `Terminal.Gui` | *(TBD Phase 2)* | Phase 2 | Full-screen TUI for resource tree explorer |
| **Scriban** | `Scriban` | *(TBD post-v1)* | Post-v1 | Advanced scripting (variables, loops, conditionals) |

### Documentation & Examples

| Library | Docs | API Examples | GitHub |
|---------|------|--------------|--------|
| **Spectre.Console** | [spectreconsole.net/console](https://spectreconsole.net/console) | [spectreconsole/examples](https://github.com/spectreconsole/examples) | [spectreconsole/spectre.console](https://github.com/spectreconsole/spectre.console) |
| **Spectre.Console.Cli** | [spectreconsole.net/cli](https://spectreconsole.net/cli) | [CLI tutorials](https://spectreconsole.net/cli/tutorials/building-a-multi-command-cli-tool) | same repo |
| **PrettyPrompt** | [GitHub README](https://github.com/waf/PrettyPrompt/blob/main/README.md) | [FruitPrompt example](https://github.com/waf/PrettyPrompt/tree/main/examples/PrettyPrompt.Examples.FruitPrompt) | [waf/PrettyPrompt](https://github.com/waf/PrettyPrompt) |

### Key API Patterns

**Spectre.Console — Table rendering:**
```csharp
var table = new Table();
table.AddColumn("Name");
table.AddColumn("Value");
table.AddRow("Health", "100.0");
AnsiConsole.Write(table);
```

**Spectre.Console — Markup output:**
```csharp
AnsiConsole.MarkupLine("[green]Success![/]");
AnsiConsole.MarkupLine("[red]Error:[/] Entity not found");
AnsiConsole.MarkupLine("[blue]Info:[/] Transaction [yellow]42[/] started");
```

**Spectre.Console.Cli — Multi-command app:**
```csharp
var app = new CommandApp();
app.Configure(config =>
{
    config.SetApplicationName("tsh");
    config.AddCommand<ReplCommand>("repl")
        .WithDescription("Start interactive REPL (default)");
    // --exec, -c handled via CommandSettings on the default command
});
return await app.RunAsync(args);
```

**Spectre.Console.Cli — CommandSettings with attributes:**
```csharp
public class TshSettings : CommandSettings
{
    [CommandArgument(0, "[database]")]
    [Description("Path to database file")]
    public string Database { get; init; }

    [CommandOption("-s|--schema")]
    [Description("Path to schema assembly")]
    public string[] Schema { get; init; }

    [CommandOption("-e|--exec")]
    [Description("Execute .tsh script file")]
    public string ExecFile { get; init; }

    [CommandOption("-c")]
    [Description("Execute single command")]
    public string Command { get; init; }

    [CommandOption("-f|--format")]
    [Description("Output format")]
    [DefaultValue("table")]
    public string Format { get; init; }
}
```

**PrettyPrompt — REPL loop with callbacks:**
```csharp
await using var prompt = new Prompt(
    persistentHistoryFilepath: "~/.tsh_history",
    callbacks: new TshPromptCallbacks(),
    configuration: new PromptConfiguration(
        prompt: new FormattedString("tsh> ")));

while (true)
{
    var response = await prompt.ReadLineAsync();
    if (response.IsSuccess)
    {
        if (response.Text == "exit") break;
        ExecuteCommand(response.Text);
    }
}
```

**PrettyPrompt — Custom callbacks (completions + highlighting):**
```csharp
internal class TshPromptCallbacks : PromptCallbacks
{
    protected override Task<IReadOnlyList<CompletionItem>> GetCompletionItemsAsync(
        string text, int caret, TextSpan spanToBeReplaced, CancellationToken ct)
    {
        // Return context-sensitive completions based on parse state
        var items = GetCompletionsForContext(text, caret, spanToBeReplaced);
        return Task.FromResult<IReadOnlyList<CompletionItem>>(items);
    }

    protected override Task<IReadOnlyCollection<FormatSpan>> HighlightCallbackAsync(
        string text, CancellationToken ct)
    {
        // Return spans with colors for keywords, components, strings, numbers
        var spans = HighlightSyntax(text);
        return Task.FromResult<IReadOnlyCollection<FormatSpan>>(spans);
    }
}
```

### Terminal Ownership Model

These libraries run **sequentially**, never simultaneously:
- **PrettyPrompt** owns the terminal during input (prompt, editing, completions)
- **Spectre.Console** owns the terminal during output (tables, diagnostics)
- **Terminal.Gui** owns the terminal during interactive sessions (alternate screen buffer)

See [Phase 1 §2](./01-phase1-core.md#2-architecture) for the full terminal ownership diagram.

## 4. Phase Roadmap

| Phase | Focus | Key Deliverables | Engine Dependencies |
|-------|-------|------------------|---------------------|
| **[Phase 1](./01-phase1-core.md)** | Core REPL + Schema + CRUD | REPL loop, `open`/`close`, schema loading, transactions, entity CRUD, output formatting, `.tsh` scripts, batch mode | None (all APIs available) |
| **[Phase 2](./02-phase2-diagnostics.md)** | Diagnostics & Inspection | Cache/segment/B+Tree/MVCC diagnostics, `memory`, interactive resource tree TUI, `resources --flat` | None (Resource Graph + direct struct access available) |
| **Post-v1** | Engine-dependent features | `query`/`count`/`find` (needs Query Engine #60), admin commands (`verify`, `compact`, `backup`, `checkpoint`), Scriban scripting, `cache-watch`, `+=` syntax | Query Engine, Backup, Verification |

Phase 1 and Phase 2 together constitute **v1** of the shell. Post-v1 features are gated on engine subsystems that don't exist yet.

## 5. Project Structure

```
src/Typhon.Shell/
├── Typhon.Shell.csproj
├── Program.cs                      # Entry point, Spectre.Console.Cli bootstrap, REPL loop
│
├── Commands/                       # Command handlers
│   ├── ICommand.cs                 # Command interface
│   ├── DatabaseCommands.cs         # open, close, info
│   ├── SchemaCommands.cs           # load-schema, reload-schema, schema, describe
│   ├── TransactionCommands.cs      # begin, commit, rollback
│   ├── DataCommands.cs             # create, read, update, delete
│   ├── DiagnosticCommands.cs       # cache-stats, segments, btree, revisions, etc.
│   └── ShellCommands.cs            # help, set, history, exit
│
├── Parsing/                        # Input parsing
│   ├── CommandParser.cs            # Input line → command + args
│   ├── Tokenizer.cs                # Lexical analysis (strings, numbers, identifiers)
│   ├── BraceExpressionParser.cs    # { Field=Value, ... } parser
│   ├── ScriptParser.cs             # .tsh file reader (line-oriented + directives)
│   └── BlockDirectiveParser.cs     # @compact/@json/@end block handler
│
├── Formatting/                     # Output rendering
│   ├── IOutputFormatter.cs         # Formatter interface
│   ├── TableFormatter.cs           # Default compact table
│   ├── FullTableFormatter.cs       # Spectre.Console bordered table
│   ├── JsonFormatter.cs            # JSON output
│   └── CsvFormatter.cs             # CSV output
│
├── Schema/                         # Schema bridge
│   ├── AssemblySchemaLoader.cs     # Load [Component] types from DLL via reflection
│   ├── ComponentSchema.cs          # Field map: name → (offset, size, FieldType)
│   └── TextToStructConverter.cs    # Parse text values → write to unmanaged buffer
│
├── Interactive/                    # Terminal.Gui sessions (Phase 2)
│   ├── ResourceTreeView.cs         # Resource navigator (TreeView<IResource>)
│   ├── ResourceDetailPanel.cs      # Right-hand detail pane
│   └── InteractiveSession.cs       # Terminal.Gui init/shutdown, alt screen buffer
│
├── Scripting/                      # (Post-v1) Scriban integration
│   ├── ScribanScriptRunner.cs
│   └── TshFunctionRegistry.cs
│
└── Session/
    ├── ShellSession.cs             # Session state (db, tx, settings, loaded schemas)
    └── PromptBuilder.cs            # Dynamic prompt generation
```

**Packaging:** Published as a .NET global tool via NuGet.

```xml
<PackAsTool>true</PackAsTool>
<ToolCommandName>tsh</ToolCommandName>
```

Installation: `dotnet tool install -g typhon-shell`

## 6. Cross-Cutting Design Decisions

These decisions apply across all phases. Phase-specific decisions are documented in their respective design documents.

### Resolved

- [x] **CLI framework: Spectre.Console + Spectre.Console.Cli** — Spectre.Console for rich output, Spectre.Console.Cli for CLI argument parsing. Replaces `System.CommandLine` (perpetual preview) and hand-rolled arg parsing.
- [x] **REPL line editing: PrettyPrompt** — Syntax highlighting, autocompletion, persistent history, multi-line input. Customized via `IPromptCallbacks`. Single dependency: `TextCopy`.
- [x] **REPL command parsing: hand-rolled parser** — REPL commands use a hand-rolled parser with a formal PEG grammar. Spectre.Console.Cli handles the outer CLI (`tsh --exec`, `tsh -c`), not the REPL loop.
- [x] **Interactive TUI: Terminal.Gui** — Full-screen interactive sessions (resource tree explorer) via alternate screen buffer. Runs sequentially with Spectre.Console/PrettyPrompt.
- [x] **Output formats: table, full-table, JSON, CSV** — Spectre.Console for terminal output; JSON and CSV for pipe/CI usage.
- [x] **Auto-commit: off by default** — Explicit `begin`/`commit` required. Safer for exploration — accidental creates/deletes aren't persisted until commit. Configurable via `set auto-commit on`.
- [x] **No built-in test components** — Users must always load a schema assembly (`load-schema`). No test components ship with the shell.
- [x] **Schema API: current `ComponentAttribute(name, revision, allowMultiple)`** — Shell works with the existing attribute constructor. No engine changes required.
- [x] **Database creation via `open`** — `open mydb.typhon` creates the file if it doesn't exist (sqlite3 convention). No `--create` flag.
- [x] **First-class product in `src/`** — `Typhon.Shell` is a shipped artifact, not a dev-only tool.
- [x] **NuGet global tool distribution** — `dotnet tool install -g typhon-shell` gives `tsh` on PATH.
- [x] **No plugins/extensions** — Scripts + Unix pipes + C# API cover extensibility needs.
- [x] **Prompt: fixed format, not configurable** — `tsh:dbname[tx:N*]>` encodes all essential state.
- [x] **Color: one built-in scheme** — Keywords=blue, components=green, strings=yellow, errors=red, diagnostics=cyan. Escape hatch: `set color off`.
- [x] **Read requires component name** — `read <id> <component>` always. No bare `read <id>` (would require iterating all ComponentTables).
- [x] **Delete requires component name** — `delete <id> <component>` always. Consistent with read.

## 7. Post-v1 Roadmap

Features explicitly deferred, with rationale and engine dependencies:

| Feature | Dependency | Rationale |
|---------|------------|-----------|
| `query` / `count` / `find` | Query Engine (#60) | Cannot implement without engine query support |
| `verify` | Integrity verification subsystem | Engine has no page checksum / B+Tree validation API |
| `compact` | Compaction subsystem | Engine has no revision GC / page reclaim API |
| `backup` / `restore` | Backup subsystem | Engine has no CoW snapshot / restore API |
| `checkpoint` | WAL / checkpoint subsystem | Engine has no explicit checkpoint API |
| Scriban scripting | Stable command model | `#!scriban` scripts need a stable function registry |
| `cache-watch` | Background thread + Live display | Requires Terminal.Gui/Spectre cooperation on bg thread |
| Update `+=` / `-=` syntax | None (engine API sufficient) | Read-modify-write sugar; low priority vs core features |
| `read <id>` (all components) | None | Requires iterating all tables; ergonomic but not essential |
| `delete <id>` (all components) | None | Same as above |
| `profile` command | None | Snapshot-diff of engine counters; `set timing on` covers 80% |

## 8. Related Documents

| Document | Relationship |
|----------|-------------|
| [Ideas: TyphonShell/](../../ideas/tooling/TyphonShell/README.md) | Original brainstorming (5 documents) |
| [ADR-004: Embedded Engine](../../adr/004-embedded-engine-no-server.md) | Why there's no client-server protocol |
| [Overview 03: Storage](../../overview/03-storage.md) | PagedMMF internals exposed by diagnostics |
| [Overview 04: Data](../../overview/04-data.md) | MVCC/revision model exposed by diagnostics |
| [Overview 08: Resources](../../overview/08-resources.md) | Resource Graph API used by `resources` navigator |
| [Overview 09: Observability](../../overview/09-observability.md) | Telemetry layer (shell reads Resource Graph directly, not through OTel) |
| [Design: QueryEngine](../QueryEngine.md) | Query system the shell will eventually expose |
