# Part 01 — Core Concepts

**Date:** 2026-02-08
**Status:** Raw idea

## Architecture

```
┌──────────────────────────────────────────────┐
│                 Typhon.Shell                 │
│                                              │
│  ┌─────────────┐  ┌──────────┐  ┌──────────┐ │
│  │  Command    │  │ Session  │  │ Output   │ │
│  │  Parser     │  │ State    │  │ Formatter│ │
│  └─────┬───────┘  └───┬──────┘  └────┬─────┘ │
│        │              │              │       │
│  ┌─────▼──────────────▼──────────────▼─────┐ │
│  │           Command Executor              │ │
│  └─────────────────┬───────────────────────┘ │
│                    │                         │
│  ┌─────────────────▼───────────────────────┐ │
│  │           DatabaseEngine                │ │
│  │  (in-process, single owner)             │ │
│  └─────────────────────────────────────────┘ │
└──────────────────────────────────────────────┘
```

### Key Components

| Component | Responsibility |
|-----------|----------------|
| **Command Parser** | Tokenize input → command object with arguments. Handles quoting, escaping, brace expressions for component data. |
| **Session State** | Tracks: current database, active transaction, registered schemas, output settings, command history. |
| **Output Formatter** | Renders results as table, JSON, or CSV depending on mode/flags. |
| **Command Executor** | Dispatches parsed commands to handler functions. Each handler interacts with the engine or session state. |
| **DatabaseEngine** | The actual Typhon engine instance, created on `open`, disposed on `close`. |

## Process Model

The shell is a single-threaded REPL loop:

```
1. Display prompt (reflects session state)
2. Read input line
3. Parse into command
4. Execute command
5. Display result
6. Goto 1
```

The single-threaded model is intentional for an interactive tool. Typhon's concurrency primitives are designed for multi-threaded in-process access, but an interactive shell doesn't need parallelism — clarity and predictability matter more.

### Terminal Ownership Model

The shell uses multiple libraries that each take control of the terminal at different times. They run **sequentially**, never simultaneously:

```
┌─────────────────────────────────────────────────────────────────┐
│                    Terminal Ownership Timeline                  │
│                                                                 │
│  PrettyPrompt          Spectre.Console        Terminal.Gui      │
│  (input phase)         (output phase)         (interactive)     │
│                                                                 │
│  ┌──────────┐          ┌─────────────┐                          │
│  │ tsh:mydb>│ ──Enter──│ table output │ ──done──► prompt again  │
│  └──────────┘          └─────────────┘                          │
│                                                                 │
│  ┌──────────┐          ┌──────────────────────┐                 │
│  │ tsh:mydb>│ ──Enter──│ Terminal.Gui session  │──q──► prompt   │
│  │resources │          │ (alternate screen)    │      again     │
│  └──────────┘          └──────────────────────┘                 │
└─────────────────────────────────────────────────────────────────┘
```

- **PrettyPrompt** owns the terminal during input (prompt, editing, completions, highlighting)
- **Spectre.Console** owns the terminal during output (tables, colors, progress bars)
- **Terminal.Gui** owns the terminal during interactive sessions (resource tree explorer)

Terminal.Gui uses the **alternate screen buffer** — the same mechanism used by `vim`, `less`, and `htop`. When the `resources` command launches, the REPL scrollback is preserved on the normal screen. When the user exits the interactive session (pressing `q`), the normal screen is restored and the REPL prompt reappears exactly where it was.

**Exception**: Long-running commands (e.g., `verify` on a large database) could optionally run on a background thread with a progress indicator, cancellable via Ctrl+C.

## Session Lifecycle

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
  │     Note: some commands auto-commit or warn if tx is dirty
  │
  └─► close / exit
        Warns if transaction is active (uncommitted changes)
        Disposes engine, releases file lock
```

### Prompt Design

The prompt is informational — it tells you where you are at a glance:

| State | Prompt | Information |
|-------|--------|-------------|
| No database | `tsh> ` | Shell is idle |
| Database open | `tsh:mydb> ` | Database name (from filename) |
| Transaction active | `tsh:mydb[tx:42]> ` | Transaction tick number |
| Transaction dirty | `tsh:mydb[tx:42*]> ` | Asterisk = uncommitted changes |

## Startup & Configuration

### Command-Line Arguments

```bash
# Interactive mode (default)
tsh

# Open (or create) a database immediately
tsh mydb.typhon

# Open + load schema assembly
tsh mydb.typhon --schema MyGame.Components.dll

# Create a new database, load schema, ready to go
tsh newgame.typhon --schema Typhon.ARPG.Schema.dll

# Batch mode: execute commands from file
tsh --exec commands.tsh

# Batch mode: execute single command
tsh mydb.typhon -c "count CompA"

# Pipe mode: read commands from stdin
echo "open mydb.typhon\ncount CompA\nclose" | tsh

# Output format override
tsh mydb.typhon --format json

# Install as global tool
dotnet tool install -g typhon-shell
```

### Configuration File (Optional, Future)

A `.tshrc` file in the user's home directory or the database directory could configure defaults:

```
# .tshrc
set format table
set page-size 20
set history-size 1000
```

## Error Handling

The shell should never crash from a user command. All command execution is wrapped in try-catch with user-friendly error messages:

```
tsh:mydb> read 999 CompA
  Error: Entity 999 not found in CompA

tsh:mydb[tx:42]> commit
  Conflict: Entity 5 CompA was modified by another transaction
  (use 'rollback' to discard changes, or resolve and retry)
```

Errors display in a distinct style (color if terminal supports it) but never dump stack traces unless `set verbose on` is enabled.

## Decisions

- [x] **Tab completion: yes** — Provided by [PrettyPrompt](https://github.com/waf/PrettyPrompt) via `IPromptCallbacks`. Completions include command names, component names, field names, and entity IDs. Documentation tooltips shown alongside completions.
- [x] **Command history: persistent across sessions** — PrettyPrompt provides built-in persistent history with prefix filtering. Stored in `~/.tsh_history`.
- [x] **Syntax highlighting: yes** — PrettyPrompt provides ANSI-based syntax highlighting. Keywords, component names, string literals, and numeric values each get distinct colors.
- [x] **Ctrl+C behavior** — PrettyPrompt provides `CancellationToken` support. Ctrl+C cancels the current input line (clears it); during command execution, it cancels the running command. Ctrl+C on an empty prompt does not exit (use `exit` command).

- [x] **Prompt: fixed format, not configurable** — The built-in prompt (`tsh:dbname[tx:N*]>`) encodes all essential state: database name, transaction tick, dirty flag. No template engine or PS1-style customization. If more state is needed later (e.g., schema name), the fixed format is updated in code. Database shells (psql, sqlite3, mongosh) ship one prompt format — nobody configures them.
- [x] **Color: one built-in scheme, no theme customization** — A single hardcoded color scheme (keywords=blue, components=green, strings=yellow, errors=red, diagnostics=cyan). PrettyPrompt and Spectre.Console handle terminal capability detection (TrueColor/256/16/none) automatically. No theme files, no user-facing color configuration. The escape hatch is `set color off` for colorblind users or minimal terminals.

## Open Questions

*(None remaining — all resolved.)*
