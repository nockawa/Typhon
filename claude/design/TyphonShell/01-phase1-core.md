# Phase 1 — Core REPL, Schema & Data Operations

**Date:** February 2026
**Status:** Draft
**Parent:** [Typhon.Shell Design](./README.md)

---

> 💡 **TL;DR:** Phase 1 delivers a working interactive shell: open a database, load a schema assembly, create/read/update/delete entities in explicit transactions, format output, and run `.tsh` scripts. Jump to [§6 (Command Reference)](#6-command-reference) for the full command spec, [§7 (Formal Grammar)](#7-formal-grammar) for the PEG grammar, or [§11 (Script Format)](#11-script-format-tsh) for batch scripting.

---

## Table of Contents

1. [Overview](#1-overview)
2. [Architecture](#2-architecture)
3. [Session Lifecycle](#3-session-lifecycle)
4. [Startup & CLI](#4-startup--cli)
5. [Schema Bridge](#5-schema-bridge)
6. [Command Reference](#6-command-reference)
7. [Formal Grammar](#7-formal-grammar)
8. [Transaction Model](#8-transaction-model)
9. [Output Formatting](#9-output-formatting)
10. [Error Handling](#10-error-handling)
11. [Script Format (.tsh)](#11-script-format-tsh)
12. [Batch Mode](#12-batch-mode)
13. [Shell Settings](#13-shell-settings)
14. [Exit Codes](#14-exit-codes)
15. [PrettyPrompt Integration](#15-prettyprompt-integration)
16. [Phase 1 Decisions](#16-phase-1-decisions)

## 1. Overview

Phase 1 delivers the foundational shell — everything needed to open a database, load component definitions from a compiled assembly, and perform entity CRUD in explicit transactions. It also includes the `.tsh` script format and batch execution modes.

**What Phase 1 includes:**
- Interactive REPL loop (PrettyPrompt)
- Outer CLI (Spectre.Console.Cli)
- Database lifecycle (`open`, `close`, `info`)
- Schema loading (`load-schema`, `reload-schema`, `schema`, `describe`)
- Transaction control (`begin`, `commit`, `rollback`)
- Entity CRUD (`create`, `read`, `update`, `delete`)
- Output formatting (table, full-table, JSON, CSV)
- `.tsh` script format with `@compact`/`@json` block directives
- Batch mode (`--exec`, `-c`, pipe)
- Shell commands (`help`, `set`, `history`, `exit`)

**What Phase 1 does NOT include:**
- Query commands (`query`, `count`, `find`) — deferred until Query Engine (#60) lands
- Diagnostic commands — all in [Phase 2](./02-phase2-diagnostics.md)
- Admin commands (`verify`, `compact`, `backup`) — engine APIs don't exist yet
- Update increment syntax (`+=`, `-=`) — deferred to post-v1
- Scriban scripting — deferred to post-v1

## 2. Architecture

### Component Diagram

```
┌──────────────────────────────────────────────────┐
│                   Typhon.Shell                   │
│                                                  │
│  ┌──────────────┐  ┌───────────┐  ┌───────────┐  │
│  │ Command      │  │ Session   │  │ Output    │  │
│  │ Parser       │  │ State     │  │ Formatter │  │
│  └──────┬───────┘  └─────┬─────┘  └─────┬─────┘  │
│         │                │              │        │
│  ┌──────▼────────────────▼──────────────▼──────┐ │
│  │              Command Executor               │ │
│  └──────────────────┬──────────────────────────┘ │
│                     │                            │
│  ┌──────────────────▼──────────────────────────┐ │
│  │              DatabaseEngine                 │ │
│  │  (in-process, single-owner)                 │ │
│  └─────────────────────────────────────────────┘ │
└──────────────────────────────────────────────────┘
```

| Component | Responsibility |
|-----------|----------------|
| **Command Parser** | Tokenize input → command object with arguments. Handles quoting, escaping, brace expressions for component data. |
| **Session State** | Tracks: current database, active transaction, registered schemas, output settings, command history reference. |
| **Output Formatter** | Renders results as table, full-table, JSON, or CSV depending on mode/flags. |
| **Command Executor** | Dispatches parsed commands to handler functions. Each handler interacts with the engine or session state. |
| **DatabaseEngine** | The Typhon engine instance, created on `open`, disposed on `close`. |

### Process Model

The shell is a single-threaded REPL loop:

```
1. Display prompt (reflects session state)
2. Read input line (PrettyPrompt)
3. Parse into command
4. Execute command
5. Display result (Spectre.Console)
6. Goto 1
```

Single-threaded is intentional for an interactive tool. Clarity and predictability matter more than parallelism.

### Terminal Ownership

```
┌────────────────────────────────────────────────────────┐
│                Terminal Ownership Timeline             │
│                                                        │
│  PrettyPrompt          Spectre.Console                 │
│  (input phase)         (output phase)                  │
│                                                        │
│  ┌──────────┐          ┌──────────────┐                │
│  │ tsh:mydb>│ ──Enter──│ table output │ ──done──► loop │
│  └──────────┘          └──────────────┘                │
└────────────────────────────────────────────────────────┘
```

In Phase 1, only two libraries share the terminal. Phase 2 adds Terminal.Gui for interactive sessions.

## 3. Session Lifecycle

### Session States

```
Shell starts
  │
  ├─► No database open
  │     Prompt: tsh>
  │     Available: open, load-schema, help, set, exit
  │
  ├─► Database open, no transaction
  │     Prompt: tsh:mydb>
  │     Available: all commands except commit/rollback
  │
  ├─► Database open, transaction active
  │     Prompt: tsh:mydb[tx:42]>
  │     Available: all commands
  │
  ├─► Database open, transaction dirty (uncommitted changes)
  │     Prompt: tsh:mydb[tx:42*]>
  │     Available: all commands
  │
  └─► close / exit
        Warns if transaction is active (uncommitted changes)
        Disposes engine, releases file lock
```

### Prompt Format

| State | Prompt | Information |
|-------|--------|-------------|
| No database | `tsh> ` | Shell is idle |
| Database open | `tsh:mydb> ` | Database name (from filename without extension) |
| Transaction active | `tsh:mydb[tx:42]> ` | Transaction tick number |
| Transaction dirty | `tsh:mydb[tx:42*]> ` | Asterisk = uncommitted changes |

The prompt format is fixed — not configurable. The built-in format encodes all essential state.

## 4. Startup & CLI

### Outer CLI (Spectre.Console.Cli)

Spectre.Console.Cli handles process-level argument parsing. This is the **outer** layer — it determines which mode the shell runs in, then hands off to the REPL loop or batch executor.

```bash
# Interactive mode (default)
tsh

# Open (or create) a database immediately
tsh mydb.typhon

# Open + load schema assembly
tsh mydb.typhon --schema MyGame.Components.dll

# Multiple schema assemblies
tsh mydb.typhon --schema Core.dll --schema World.dll

# Batch mode: execute commands from file
tsh --exec commands.tsh

# Batch mode with database
tsh mydb.typhon --exec commands.tsh

# Single command mode
tsh mydb.typhon -c "count CompA"

# Pipe mode: read from stdin
echo "open mydb.typhon" | tsh

# Output format override
tsh mydb.typhon --format json

# Combined: database + schema + batch
tsh mydb.typhon --schema Game.dll --exec setup.tsh
```

### CLI Argument Spec

| Argument | Short | Type | Description |
|----------|-------|------|-------------|
| `[database]` | — | positional | Path to database file (optional). Opens on start if provided. |
| `--schema` | `-s` | string (repeatable) | Path to schema assembly DLL. Loads on start. |
| `--exec` | `-e` | string | Path to `.tsh` script file. Executes and exits. |
| `-c` | — | string | Single command to execute. Runs and exits. |
| `--format` | `-f` | enum | Output format: `table`, `full-table`, `json`, `csv`. Default: `table`. |
| `--version` | — | flag | Print version and exit. |
| `--help` | `-h` | flag | Print help and exit. |

### Mode Precedence

1. If `-c` is provided → **single-command mode** (execute, print result, exit)
2. If `--exec` is provided → **script mode** (execute file, exit)
3. If stdin is not a terminal → **pipe mode** (read commands from stdin, exit)
4. Otherwise → **interactive mode** (REPL loop)

In modes 1-3, `--format` defaults to `table` (overridable). In interactive mode, `set format` controls the output format dynamically.

## 5. Schema Bridge

### The Problem

Typhon components are compiled C# structs. The shell works with text. The schema bridge converts text input like `{ PlayerId=1, Health=100.0 }` into a typed, blittable struct that the engine can store.

### Assembly Loading Pipeline

```
load-schema "MyGame.dll"
     │
     ▼
1. Assembly.LoadFrom(path)
     │
     ▼
2. Scan for [Component] attribute on structs
     │
     ▼
3. For each component:
   a. Read ComponentAttribute (name, revision, allowMultiple)
   b. Read [Field] attributes → (name, offset, size, FieldType)
   c. Read [Index] attributes → (unique/multi, field type)
     │
     ▼
4. RegisterComponentFromAccessor<T>() via reflection (MakeGenericMethod)
     │
     ▼
5. Build ComponentSchema: field names → (offset, size, FieldType)
     │
     ▼
6. Store in SchemaRegistry (component name → ComponentSchema)
```

**Multiple assemblies** are supported. Each `load-schema` call adds to the registry:

```
tsh> load-schema "Core.dll"
  Loaded 2 components: PlayerStats, Inventory
tsh> load-schema "World.dll"
  Loaded 3 components: Position, Terrain, Weather
tsh> schema
  5 components loaded from 2 assemblies
```

### Reload Workflow

`reload-schema` performs a full engine restart — close database, dispose engine, reload assemblies from disk, reopen:

```
tsh:mydb> reload-schema
  Warning: database will be closed and reopened. Continue? (y/n) y
  Closing database...
  Reloading assemblies...
  Reopening mydb.typhon...
  Loaded 5 components. Database ready.
```

`AssemblyLoadContext` unloading was rejected — it requires no lingering references to types from the old context, which is nearly impossible when types are registered as generic parameters throughout the engine (`ComponentTable<T>`, indexes, etc.).

### Text-to-Struct Conversion

```
Text Input → Input Parser → (fieldName, textValue) pairs → Type Converter → Binary Buffer → Engine
                ↑                                             ↑
         (format-specific)                            (FieldType-driven)
```

1. **Parse input** — Format-specific parser produces `(fieldName, textValue)` pairs
2. **Resolve fields** — Look up each field name in `ComponentSchema` → (offset, size, `FieldType`)
3. **Convert values** — Parse text according to `FieldType` (see type table)
4. **Write to buffer** — Allocate unmanaged buffer of struct size, write each value at its offset
5. **Pass to engine** — Use buffer as `ref T` via unsafe pointer cast

### Field Type Table

| FieldType | C# Type | Text Example | Parse Notes |
|-----------|---------|-------------|-------------|
| Boolean | `bool` | `true`, `false` | Case-insensitive |
| Byte | `sbyte` | `42` | Signed, -128..127 |
| UByte | `byte` | `200` | Unsigned, 0..255 |
| Char | `char` | `'A'` | Single-quoted character |
| Short | `short` | `-1000` | |
| UShort | `ushort` | `60000` | |
| Int | `int` | `42` | Default for unadorned integers |
| UInt | `uint` | `42u` | Suffix `u` |
| Float | `float` | `95.5` | Default for unadorned decimals |
| Double | `double` | `95.5d` | Suffix `d` |
| Long | `long` | `123456789L` | Suffix `L` |
| ULong | `ulong` | `42uL` | Suffix `uL` |
| String64 | `String64` | `"hello"` | Double-quoted, max 63 UTF-8 bytes |
| String1024 | `String1024` | `"longer text"` | Double-quoted, max ~1023 bytes |
| VarString | `string` | `"any length"` | Double-quoted, stored in VSB segment |
| Point2F | `Point2F` | `(1.0, 2.0)` | Parenthesized tuple |
| Point3F | `Point3F` | `(1.0, 2.0, 3.0)` | |
| Point4F | `Point4F` | `(1, 2, 3, 4)` | |
| Point2D | `Point2D` | `(1.0d, 2.0d)` | Double-precision |
| Point3D | `Point3D` | `(1.0d, 2.0d, 3.0d)` | |
| Point4D | `Point4D` | `(1.0d, 2.0d, 3.0d, 4.0d)` | |
| QuaternionF | `QuaternionF` | `(0, 0, 0, 1)` | X, Y, Z, W |
| QuaternionD | `QuaternionD` | `(0d, 0d, 0d, 1d)` | |
| Variant | `Variant` | `variant("si:42")` | Explicit type-tagged format |

**Type inference:** The converter knows the target type from the schema. `Health=95` is unambiguous when the schema says `Health` is `Float` — it parses as `95.0f`. Explicit suffixes (`d`, `L`, `u`) are only needed to override inference or in ambiguous contexts.

### Input Formats

#### Brace Format (Interactive Default)

Terse, field-name-driven. The primary format for interactive use:

```
create PlayerStats { PlayerId=1, Health=100.0, Score=0 }
```

Rules:
- Curly braces delimit the expression
- Comma-separated `Field=Value` pairs
- String values double-quoted: `Name="Alice"`
- Tuples use parentheses: `Pos=(1.0, 2.0, 3.0)`
- Whitespace is flexible

#### Compact Format (Script Bulk Data)

Positional values matched to schema field order. Used in `.tsh` scripts via `@compact` block directive:

```
@compact PlayerStats
1, 100.0, 0
2, 85.5, 12
3, 92.0, 5
@end
```

Fields matched by position (schema order). Faster to write for bulk operations, easier to generate from external tools.

#### JSON Format (Pipe/CI)

Self-describing, machine-readable. Used via `@json` block directive or pipe input:

```
@json PlayerStats
[
  { "PlayerId": 1, "Health": 100.0, "Score": 0 },
  { "PlayerId": 2, "Health": 85.5, "Score": 12 }
]
@end
```

Auto-detects JSON array (first non-whitespace is `[`) vs NDJSON (one object per line).

#### Format Summary

| Format | Best For | Verbosity | Machine-Friendly |
|--------|----------|-----------|-------------------|
| **Brace** | Interactive REPL | Medium | No |
| **Compact** | Bulk script data | Low | Somewhat |
| **JSON** | CI/CD, pipes | High | Yes |

## 6. Command Reference

### Phase 1 Command Summary

| Category | Command | Requires DB | Requires TX | Description |
|----------|---------|:-----------:|:-----------:|-------------|
| **Database** | `open <path>` | — | — | Open (or create) a database file |
| | `close` | Yes | — | Close current database |
| | `info` | Yes | — | Show database summary |
| **Schema** | `load-schema <path>` | — | — | Load component types from assembly |
| | `reload-schema` | Yes | — | Close, reload assemblies, reopen |
| | `schema` | — | — | List loaded components |
| | `describe <component>` | — | — | Show component field layout |
| **Transaction** | `begin` | Yes | — | Start a new transaction |
| | `commit` | Yes | Yes | Commit current transaction |
| | `rollback` | Yes | Yes | Rollback current transaction |
| **Data** | `create [#id] <comp> <brace>` | Yes | Yes* | Create an entity |
| | `read <id> <comp>` | Yes | — | Read entity component data |
| | `update <id> <comp> <brace>` | Yes | Yes* | Update entity component data |
| | `delete <id> <comp>` | Yes | Yes* | Delete entity component |
| **Shell** | `set [key [value]]` | — | — | View/change shell settings |
| | `help [command]` | — | — | Show help |
| | `history` | — | — | Show command history |
| | `exit` / `quit` | — | — | Exit the shell |

*Data commands require a transaction. If `auto-commit` is on, the shell wraps each data command in an implicit transaction.

### Command Details

#### `open <path>`

Opens a database file. Creates the file if it doesn't exist (sqlite3 convention).

```
tsh> open mydb.typhon
  Opened mydb.typhon (new database created)

tsh> open existing.typhon
  Opened existing.typhon (512 pages, 3 components)
```

- Closes any currently open database first (with warning if transaction is active)
- Registers any previously loaded schema assemblies with the new engine instance
- Only one database can be open at a time

#### `close`

Closes the current database and releases the file lock.

```
tsh:mydb> close
  Database closed.

tsh:mydb[tx:42*]> close
  Warning: active transaction with uncommitted changes. Close anyway? (y/n)
```

#### `info`

Shows database summary information.

```
tsh:mydb> info
  Database: mydb.typhon
  ──────────────────────────────
  File size:       4.2 MB
  Total pages:     512
  Used pages:      487
  Free pages:      25
  Segments:        8
  Components:      3 (PlayerStats, Inventory, Position)
  Created:         2026-02-08 09:15:00
  Last modified:   2026-02-08 10:31:12
```

#### `load-schema <path>`

Loads component types from a compiled .NET assembly.

```
tsh> load-schema "MyGame.Components.dll"
  Loaded 3 components: PlayerStats, Inventory, Position
```

- Can be called before or after opening a database
- Multiple assemblies can be loaded (additive)
- If a database is open, registers newly discovered components with the engine

#### `reload-schema`

Closes the database, reloads all assemblies from disk, and reopens.

```
tsh:mydb> reload-schema
  Warning: database will be closed and reopened. Continue? (y/n) y
  Closing database...
  Reloading assemblies...
  Reopening mydb.typhon...
  Loaded 5 components. Database ready.
```

#### `schema`

Lists all loaded component types.

```
tsh> schema
  5 components loaded from 2 assemblies:
    PlayerStats   (Core.dll)
    Inventory     (Core.dll)
    Position      (World.dll)
    Terrain       (World.dll)
    Weather       (World.dll)
```

#### `describe <component>`

Shows the field layout of a component type.

```
tsh> describe PlayerStats
  PlayerStats [12 bytes]
  ──────────────────────────────
  PlayerId    Int32      offset=0   [indexed, unique]
  Health      Single     offset=4
  Score       Int32      offset=8
```

#### `begin`

Starts a new transaction.

```
tsh:mydb> begin
  Transaction started (tick 100)
```

- Error if a transaction is already active
- Prompt changes to show transaction tick

#### `commit`

Commits the current transaction.

```
tsh:mydb[tx:100*]> commit
  Committed: 3 creates, 1 update, 0 deletes
```

- Reports the operation counts from the committed transaction
- On conflict: reports the conflicting entity/component and suggests rollback

```
tsh:mydb[tx:100*]> commit
  Conflict: Entity 5 PlayerStats was modified by another transaction
  (use 'rollback' to discard changes, or resolve and retry)
```

#### `rollback`

Rolls back the current transaction.

```
tsh:mydb[tx:100*]> rollback
  Rolled back (3 pending operations discarded)
```

#### `create [#id] <component> <brace-expr>`

Creates an entity with the given component data.

```
# Engine assigns entity ID
tsh:mydb[tx:100]> create PlayerStats { PlayerId=1, Health=100.0, Score=0 }
  Entity 7 created

# Explicit entity ID
tsh:mydb[tx:100]> create #42 PlayerStats { PlayerId=2, Health=85.5, Score=12 }
  Entity 42 created

# Multi-component entity: same explicit ID, separate commands
tsh:mydb[tx:100]> create #50 PlayerStats { PlayerId=3, Health=100.0, Score=0 }
  Entity 50 created
tsh:mydb[tx:100]> create #50 Inventory { Slots=20 }
  Entity 50 updated (Inventory added)
```

#### `read <id> <component>`

Reads entity data for a specific component.

```
tsh:mydb> read 1 PlayerStats
  Entity 1 | PlayerId=1  Health=100.0  Score=0
```

- Can read outside a transaction (uses a temporary auto-transaction for snapshot consistency)
- Within a transaction, reads reflect the transaction's snapshot + its own uncommitted changes

#### `update <id> <component> <brace-expr>`

Updates entity component data. All specified fields are overwritten; unspecified fields remain unchanged.

```
tsh:mydb[tx:100]> update 1 PlayerStats { Health=95.0, Score=10 }
  Entity 1 updated (rev 2)
```

- Performs read-then-write internally: reads current values, overlays the provided fields, writes back

#### `delete <id> <component>`

Deletes an entity's component.

```
tsh:mydb[tx:100]> delete 1 PlayerStats
  Entity 1 PlayerStats deleted
```

#### `set [key [value]]`

View or change shell settings.

```
# Show all settings
tsh> set
  Current settings:
    format:       table
    auto-commit:  off
    verbose:      off
    page-size:    20
    color:        auto
    timing:       off

# Change a setting
tsh> set timing on
tsh> set format json
```

See [§13 (Shell Settings)](#13-shell-settings) for the full settings table.

#### `help [command]`

Shows help for all commands or a specific command.

```
tsh> help
  Available commands:
    open <path>              Open (or create) a database
    close                    Close current database
    ...

tsh> help create
  create [#id] <component> { field=value, ... }
    Creates an entity with the given component data.
    ...
```

#### `history`

Shows recent command history.

```
tsh> history
  1: open mydb.typhon
  2: load-schema Game.dll
  3: begin
  4: create PlayerStats { PlayerId=1, Health=100.0, Score=0 }
  ...
```

#### `exit` / `quit`

Exits the shell. Warns if a transaction is active or database is open with unsaved changes.

## 7. Formal Grammar

The following PEG grammar defines the complete syntax for Phase 1 commands. Each REPL input line is parsed as a single `Command`. Script files are parsed as a `Script`.

```peg
# ═══════════════════════════════════════════════════════════
# Typhon Shell — Phase 1 PEG Grammar
# ═══════════════════════════════════════════════════════════

# ── Top Level ──────────────────────────────────────────────

Script          <- (Line EOL)* Line? EOF
Line            <- WS? (Comment / DirectiveBlock / Command) WS?
                 / WS?                                          # blank line

Comment         <- '#' (!EOL .)*

# ── Commands ───────────────────────────────────────────────

Command         <- DatabaseCmd / SchemaCmd / TransactionCmd
                 / DataCmd / ShellCmd

# ── Database Commands ──────────────────────────────────────

DatabaseCmd     <- OpenCmd / CloseCmd / InfoCmd

OpenCmd         <- 'open' WS Path
CloseCmd        <- 'close'
InfoCmd         <- 'info'

# ── Schema Commands ────────────────────────────────────────

SchemaCmd       <- LoadSchemaCmd / ReloadSchemaCmd
                 / SchemaListCmd / DescribeCmd

LoadSchemaCmd   <- 'load-schema' WS Path
ReloadSchemaCmd <- 'reload-schema'
SchemaListCmd   <- 'schema'
DescribeCmd     <- 'describe' WS ComponentName

# ── Transaction Commands ───────────────────────────────────

TransactionCmd  <- BeginCmd / CommitCmd / RollbackCmd

BeginCmd        <- 'begin'
CommitCmd       <- 'commit'
RollbackCmd     <- 'rollback'

# ── Data Commands ──────────────────────────────────────────

DataCmd         <- CreateCmd / ReadCmd / UpdateCmd / DeleteCmd

CreateCmd       <- 'create' (WS EntityIdPrefix)? WS ComponentName
                   WS BraceExpr
ReadCmd         <- 'read' WS EntityId WS ComponentName
UpdateCmd       <- 'update' WS EntityId WS ComponentName
                   WS BraceExpr
DeleteCmd       <- 'delete' WS EntityId WS ComponentName

EntityIdPrefix  <- '#' UnsignedInt
EntityId        <- UnsignedInt

# ── Shell Commands ─────────────────────────────────────────

ShellCmd        <- SetCmd / HelpCmd / HistoryCmd / ExitCmd

SetCmd          <- 'set' (WS SettingKey (WS SettingValue)?)?
HelpCmd         <- 'help' (WS CommandName)?
HistoryCmd      <- 'history'
ExitCmd         <- 'exit' / 'quit'

SettingKey      <- Identifier
SettingValue    <- Identifier / UnsignedInt
CommandName     <- Identifier ('-' Identifier)*

# ── Brace Expression ──────────────────────────────────────

BraceExpr       <- '{' WS? FieldAssignment
                   (WS? ',' WS? FieldAssignment)* WS? '}'
FieldAssignment <- FieldName WS? '=' WS? Value
FieldName       <- Identifier

# ── Values ─────────────────────────────────────────────────

Value           <- VariantLiteral / StringLiteral / CharLiteral
                 / BoolLiteral / TupleLiteral / NumberLiteral

StringLiteral   <- '"' StringChar* '"'
StringChar      <- '\\' EscapeChar / (!'"' !EOL .)
EscapeChar      <- [\\/"bfnrt]

CharLiteral     <- "'" (!"'" !EOL .) "'"

BoolLiteral     <- 'true' / 'false'

TupleLiteral    <- '(' WS? NumberLiteral
                   (WS? ',' WS? NumberLiteral)+ WS? ')'

VariantLiteral  <- 'variant' WS? '(' WS? StringLiteral WS? ')'

NumberLiteral   <- Sign? Digits ('.' Digits)? NumberSuffix?
Sign            <- '-'
Digits          <- [0-9]+
NumberSuffix    <- 'uL' / 'UL' / 'ul'
                 / 'u' / 'U'
                 / 'L' / 'l'
                 / 'd' / 'D'
                 / 'f' / 'F'

# ── Block Directives (Script Only) ────────────────────────

DirectiveBlock  <- CompactBlock / JsonBlock

CompactBlock    <- '@compact' WS ComponentName EOL
                   CompactRow*
                   WS? '@end'
CompactRow      <- WS? Value (WS? ',' WS? Value)* WS? EOL

JsonBlock       <- '@json' WS ComponentName EOL
                   JsonData
                   WS? '@end'
JsonData        <- (!('@end') .)*              # delegated to JSON parser

# ── Terminals ──────────────────────────────────────────────

ComponentName   <- [A-Z] [a-zA-Z0-9_]*        # PascalCase by convention
Identifier      <- [a-zA-Z_] [a-zA-Z0-9_]*
UnsignedInt     <- [0-9]+

Path            <- QuotedPath / BarePath
QuotedPath      <- '"' (!'"' !EOL .)* '"'
BarePath        <- [^ \t\r\n"]+               # no spaces, no quotes

WS              <- [ \t]+
EOL             <- '\r\n' / '\n'
EOF             <- !.
```

### Grammar Notes

1. **Command keywords are case-sensitive.** `open` works, `OPEN` does not.
2. **Component names are PascalCase by convention** but the grammar accepts any identifier starting with an uppercase letter. The actual validation happens during schema lookup.
3. **Block directives (`@compact`, `@json`, `@end`) are only valid in script files**, not in REPL mode. The REPL parser rejects lines starting with `@`.
4. **JSON block content is not parsed by this grammar.** The `JsonData` rule captures everything between `@json` and `@end`, then delegates to `System.Text.Json` for parsing.
5. **String escaping** supports standard C# escape sequences: `\\`, `\"`, `\/`, `\b`, `\f`, `\n`, `\r`, `\t`.
6. **Number suffix precedence**: `uL` is checked before `u` to avoid partial matching.

## 8. Transaction Model

### Explicit Transactions (Default)

By default, `auto-commit` is off. Data commands require an explicit `begin`:

```
tsh:mydb> create PlayerStats { PlayerId=1, Health=100.0 }
  Error: No active transaction. Use 'begin' to start one, or 'set auto-commit on'.
```

This is a deliberate safety net for interactive exploration — accidental creates/deletes aren't persisted until you explicitly commit.

### Auto-Commit Mode

When `auto-commit` is on, each data command is wrapped in an implicit transaction:

```
tsh:mydb> set auto-commit on
tsh:mydb> create PlayerStats { PlayerId=1, Health=100.0, Score=0 }
  Entity 1 created (auto-committed)
```

- The implicit transaction is created before the command and committed after
- If the command fails, the implicit transaction is rolled back
- `begin` is still valid in auto-commit mode — it overrides auto-commit until `commit`/`rollback`

### Read Outside Transaction

`read` commands work without an active transaction. The shell creates a temporary transaction for snapshot consistency, performs the read, and immediately disposes it:

```
tsh:mydb> read 1 PlayerStats
  Entity 1 | PlayerId=1  Health=100.0  Score=0
```

### Conflict Handling

On commit conflict, the shell reports the conflict and leaves the transaction active:

```
tsh:mydb[tx:100*]> commit
  Conflict: Entity 5 PlayerStats was modified by another transaction
  Transaction still active. Use 'rollback' to discard, or 'update' and retry 'commit'.
```

The shell uses Typhon's default "last write wins" conflict resolution. Custom conflict handlers are not exposed in v1 — they require programmatic callbacks better suited to the C# API.

## 9. Output Formatting

### Format Modes

| Mode | Trigger | Description |
|------|---------|-------------|
| `table` | Default | Compact inline format: `Entity 1 \| Field=Value  Field=Value` |
| `full-table` | `set format full-table` | Spectre.Console bordered table with column headers |
| `json` | `set format json` | JSON array of objects |
| `csv` | `set format csv` | CSV with header row |

### Examples

**table (default):**
```
tsh:mydb> read 1 PlayerStats
  Entity 1 | PlayerId=1  Health=100.0  Score=0
```

**full-table:**
```
tsh:mydb> read 1 PlayerStats
  ┌──────────┬──────────┬────────┬───────┐
  │ EntityId │ PlayerId │ Health │ Score │
  ├──────────┼──────────┼────────┼───────┤
  │        1 │        1 │  100.0 │     0 │
  └──────────┴──────────┴────────┴───────┘
```

**json:**
```json
[{ "entityId": 1, "PlayerId": 1, "Health": 100.0, "Score": 0 }]
```

**csv:**
```
EntityId,PlayerId,Health,Score
1,1,100.0,0
```

### Timing

When `set timing on`, execution time is appended to every command's output:

```
tsh:mydb> read 1 PlayerStats
  Entity 1 | PlayerId=1  Health=100.0  Score=0
  (0.08 ms)
```

## 10. Error Handling

### Principles

1. **The shell never crashes from a user command.** All command execution is wrapped in try-catch.
2. **User-friendly error messages.** No stack traces unless `set verbose on`.
3. **Distinct error styling.** Errors displayed in red (Spectre.Console markup).
4. **Context-aware suggestions.** Errors include hints when possible.

### Error Categories

| Category | Style | Example |
|----------|-------|---------|
| **Syntax error** | `Syntax error: ...` | `Syntax error: expected '}' after field assignments` |
| **Command error** | `Error: ...` | `Error: No active transaction` |
| **Schema error** | `Error: ...` | `Error: Component 'Foo' not found. Loaded: PlayerStats, Inventory` |
| **Engine error** | `Error: ...` | `Error: Entity 999 not found in PlayerStats` |
| **Conflict** | `Conflict: ...` | `Conflict: Entity 5 PlayerStats was modified by another transaction` |

### Verbose Mode

```
tsh> set verbose on
tsh:mydb> read 999 PlayerStats
  Error: Entity 999 not found in PlayerStats
  at Typhon.Engine.Transaction.ReadEntity[T](Int64 pk, T& component)
  at Typhon.Shell.Commands.DataCommands.ExecuteRead(...)
  ...
```

## 11. Script Format (.tsh)

### Design Rationale

The `.tsh` format follows the **sqlite3 model**: one command per line, composable with Unix pipes and host shell scripts. No variables, no control flow, no expressions. Complex automation belongs in bash/PowerShell or in C# code.

The only structural extension is **data block directives** (`@compact`, `@json`), which are parser mode switches for bulk data ergonomics — not programming constructs.

### Format Rules

- One command per line (identical to REPL input)
- Lines starting with `#` (optionally preceded by whitespace) are comments
- Blank lines are ignored
- `@compact` / `@json` / `@end` are the only directives
- No variables, no control flow, no expressions
- No line continuation (`\` at end of line)
- Encoding: UTF-8

### Block Directives

Block directives wrap an implicit create cycle. Each data row inside a `@compact` block becomes a `create` operation for the named component. The `@json` block parses JSON and creates one entity per element.

```bash
# setup.tsh
open test.typhon
load-schema "Game.dll"

begin

# Brace format — one at a time
create PlayerStats { PlayerId=1, Health=100.0, Score=0 }
create PlayerStats { PlayerId=2, Health=85.5, Score=12 }

# Compact format — bulk positional data
@compact PlayerStats
3, 92.0, 5
4, 78.0, 0
5, 91.2, 8
@end

# JSON format — machine-generated data
@json Inventory
[
  { "Slots": 20 },
  { "Slots": 30 }
]
@end

commit
close
```

### Error Behavior

Scripts halt on the first error. The failing line number and command are reported to stderr:

```
$ tsh --exec broken.tsh
broken.tsh:7: Error: Component 'Nonexistent' not found
(script halted)
```

## 12. Batch Mode

### Script Execution (`--exec`)

```bash
tsh --exec setup.tsh
tsh mydb.typhon --exec setup.tsh          # pre-open database
tsh mydb.typhon --schema Game.dll --exec setup.tsh  # pre-load schema
```

### Single Command (`-c`)

```bash
tsh mydb.typhon -c "info"
tsh mydb.typhon --format json -c "read 1 PlayerStats"
```

### Pipe Mode

```bash
echo "open mydb.typhon\ninfo\nclose" | tsh
python generate_data.py | tsh
```

### Composability

The deliberate absence of built-in scripting means CI/CD logic lives in the host shell:

```bash
#!/bin/bash
# Uses shell variables and conditionals — not tsh features
tsh test.typhon --schema Game.dll --exec fixtures/setup.tsh
RESULT=$(tsh test.typhon --format json -c "read 1 PlayerStats")
echo "$RESULT" | jq '.Health'
```

## 13. Shell Settings

| Setting | Values | Default | Description |
|---------|--------|---------|-------------|
| `format` | `table`, `full-table`, `json`, `csv` | `table` | Output format |
| `auto-commit` | `on`, `off` | `off` | Auto-commit data commands |
| `verbose` | `on`, `off` | `off` | Show stack traces on error |
| `page-size` | positive integer | `20` | Default row limit for data display |
| `color` | `auto`, `on`, `off` | `auto` | Color output (`auto` detects terminal) |
| `timing` | `on`, `off` | `off` | Show execution time per command |

Settings are session-scoped — they reset when the shell exits. A future `.tshrc` file could persist defaults.

## 14. Exit Codes

| Code | Meaning |
|------|---------|
| 0 | Success |
| 1 | Command error (syntax, invalid arguments) |
| 2 | Database error (file not found, corrupt, lock conflict) |
| 10 | Unhandled exception (bug in shell) |

In batch mode (`--exec` or pipe), the exit code reflects the **first error** encountered (execution halts on error).

## 15. PrettyPrompt Integration

### IPromptCallbacks Implementation

PrettyPrompt's `IPromptCallbacks` interface is the customization point. The shell provides callbacks for:

| Callback | Implementation |
|----------|----------------|
| **Completions** | Context-sensitive: command names at start of line → component names after `create`/`read`/`update`/`delete`/`describe` → field names inside `{ }` → setting names after `set` → setting values after `set <key>` |
| **Syntax highlighting** | Keywords (`begin`, `commit`, `create`, `where`) in blue. Component names in green. String literals in yellow. Numeric values in default. Errors in red. |
| **History** | Persistent across sessions in `~/.tsh_history`. PrettyPrompt provides prefix filtering. |
| **Cancellation** | Ctrl+C cancels current input line. During command execution, Ctrl+C cancels the running command via `CancellationToken`. Ctrl+C on empty prompt does not exit. |

### Completion Strategy

Completions are derived from the current parse state:

| Input State | Completion Source |
|-------------|-------------------|
| Empty / start of line | All command names |
| After `create` / `read` / `update` / `delete` / `describe` | Registered component names |
| After `load-schema` | File paths (`.dll` files) |
| After `open` | File paths (`.typhon` files) |
| Inside `{ }` | Field names for the current component |
| After `set` | Setting names |
| After `set <key>` | Valid values for that setting |
| After `help` | Command names |

## 16. Phase 1 Decisions

### Resolved

- [x] **Entity ID: engine-assigned by default, `#ID` prefix for explicit** — `create PlayerStats { ... }` auto-assigns. `create #42 PlayerStats { ... }` uses explicit ID.
- [x] **Multi-component create: separate commands** — Same explicit entity ID, separate `create` calls. No `+` operator syntax in v1.
- [x] **No cross-component queries** — Shell doesn't invent query semantics the engine doesn't support.
- [x] **Pagination: manual `limit`/`offset` via `page-size` setting** — `set page-size 20` acts as default display limit. No automatic pager.
- [x] **Assembly reload: full engine restart** — `reload-schema` closes, disposes, reloads, reopens. Reliable and takes milliseconds.
- [x] **Compact format: positional-by-schema-order only** — No named-but-terse variant.
- [x] **JSON input: auto-detect array vs NDJSON** — First non-whitespace `[` → JSON array, otherwise NDJSON.
- [x] **Partial update semantics** — `update` reads current values, overlays specified fields, writes back. Unspecified fields are preserved.
- [x] **Read/delete: component name always required** — No bare `read <id>` or `delete <id>`. Consistent and avoids hidden iteration over all tables.
