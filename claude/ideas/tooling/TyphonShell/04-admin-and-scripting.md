# Part 04 — Administration & Scripting

**Date:** 2026-02-08
**Status:** Raw idea

## Administration Commands

### Database Information

```
tsh:mydb> info
  Database: mydb.typhon
  ──────────────────────────────────────
  File size:       4.2 MB
  Total pages:     512
  Used pages:      487
  Free pages:      25
  Segments:        8
  Components:      3 (PlayerStats, Inventory, Position)

  Created:         2026-02-08 09:15:00
  Last modified:   2026-02-08 10:31:12
  Last checkpoint:  2026-02-08 10:30:00
```

### Integrity Verification

```
tsh:mydb> verify
  Verifying database integrity...
  [1/5] Page checksums...           ✓  (512 pages verified)
  [2/5] Segment occupancy bitmaps... ✓  (8 segments verified)
  [3/5] B+Tree structure...          ✓  (4 indexes verified)
  [4/5] Revision chain consistency... ✓  (1,024 chains verified)
  [5/5] Cross-reference validation... ✓  (PK → revision → data links valid)

  Database integrity: OK
```

On failure:
```
  [3/5] B+Tree structure...          ✗
    CompA.PlayerId: leaf node 42 has unsorted keys at position 12
    CompA.PlayerId: internal node 27 points to non-existent child chunk 999

  Database integrity: 2 ERRORS FOUND
  Run 'verify --repair' to attempt automatic repair (CREATES BACKUP FIRST)
```

### Checkpoint & Compaction

```
tsh:mydb> checkpoint
  Checkpoint complete (34 dirty pages flushed)

tsh:mydb> compact
  Compacting database...
  Revision GC:     removed 1,800 old revisions (MinTick threshold: 150)
  Page reclaim:    freed 12 pages (96 KB)
  Defragmentation: relocated 3 segments for contiguity
  Compact complete
```

### Backup & Restore

```
tsh:mydb> backup "mydb-backup-20260208.typhon"
  Backup started...
  Copying 487 used pages...
  Backup complete: mydb-backup-20260208.typhon (3.8 MB)

# Restore would require closing the current database first
tsh> restore "mydb-backup-20260208.typhon" "mydb-restored.typhon"
  Restoring to mydb-restored.typhon...
  Restore complete. Open with: open mydb-restored.typhon
```

## Scripting & Batch Mode

### Script File Format (`.tsh`) — Specification

> **Decision:** The `.tsh` format is **line-oriented with data block directives**. No variables, no control flow, no expressions. Complex automation belongs in the host shell (bash, PowerShell) or in C# code using the engine API directly.

#### Design Rationale

The script format deliberately follows the **sqlite3 model**: one command per line, composable with Unix pipes and shell scripts. This was chosen over two alternatives:

1. **Block-aware DSL** (variables, `if`/`foreach`): Rejected because it creates scope creep toward a full language. Every variable system needs conditionals, every conditional needs loops, every loop needs functions. Typhon already has a real language — C#. CI/CD pipelines already have bash/PowerShell. A `.tsh`-level scripting language would be an ad-hoc, inferior version of both.

2. **External host language** (embedded Lua/Python): Rejected as massive implementation effort that duplicates what the C# API already provides. The shell is a dev tool, not a programming environment.

The **one concession** to pure line-orientation is **data block directives** (`@compact`, `@json`). These are justified because they solve a data ergonomics problem — not a control flow problem. They are parser mode switches ("the next N lines follow grammar X") rather than programming constructs.

#### Format Grammar

```
script     = line*
line       = blank | comment | command | directive-block
blank      = /^\s*$/
comment    = /^\s*#.*/
command    = <any valid tsh command, identical to REPL input>
directive  = '@compact' component-name | '@json' component-name
end        = '@end'
directive-block = directive LF data-lines LF end
```

**Rules:**
- One command per line (no multi-line commands, no `;` separators)
- Lines starting with `#` (optionally preceded by whitespace) are comments
- Blank lines are ignored
- `@compact` / `@json` / `@end` are the **only** directives — they switch the parser into a data format mode (see [Part 02](./02-schema-and-data.md) for format details)
- No variables (`$name`), no control flow (`if`, `foreach`), no expressions
- No line continuation (no `\` at end of line)
- Encoding: UTF-8

#### Basic Example

```bash
# setup.tsh — Create test data for development
open test.typhon

begin
create SampleA { Id=1, Value=100, Score=95.5 }
create SampleA { Id=2, Value=200, Score=87.3 }
create SampleA { Id=3, Value=300, Score=92.1 }
commit

# Verify
count SampleA
query SampleA order by Score desc

close
```

#### Bulk Data with Block Directives

```bash
# bulk-load.tsh — Populate test dataset
open test.typhon

begin

# Compact format: positional values matched to schema field order
@compact SampleA
1, 100, 95.5
2, 200, 87.3
3, 300, 92.1
4, 400, 78.0
5, 500, 91.2
@end

# JSON format: self-describing, ideal for machine-generated scripts
@json SampleB
[
  { "Key": 1, "Name": "Alice" },
  { "Key": 2, "Name": "Bob" }
]
@end

commit
close
```

Block directives wrap an implicit `begin`/`create`/`commit` cycle — each data row inside a `@compact` block becomes a `create` operation for the named component. The `@json` block parses the JSON array and creates one entity per element.

#### Execution

```bash
tsh --exec setup.tsh
```

#### Error Behavior in Scripts

Scripts halt on the first error by default. The failing line number and command are reported to stderr. This is the safe default — partial execution of a script is usually worse than stopping early.

```
$ tsh --exec broken.tsh
broken.tsh:7: Error: Component 'Nonexistent' not found
(script halted)
```

### Pipe Mode

Commands can be piped via stdin, making the shell composable with other tools:

```bash
# Quick entity count
echo "open mydb.typhon\ncount PlayerStats\nclose" | tsh

# Generate test data from an external source
python generate_test_data.py | tsh

# Extract data as CSV for analysis
echo "open mydb.typhon\nset format csv\nquery PlayerStats\nclose" | tsh > players.csv
```

### Single Command Mode

For CI/CD one-liners:

```bash
# Check entity count in CI
tsh mydb.typhon -c "count PlayerStats"

# Verify integrity in CI pipeline
tsh mydb.typhon -c "verify" || echo "INTEGRITY CHECK FAILED"

# Export data
tsh mydb.typhon --format json -c "query PlayerStats" > players.json
```

### Composability Over Built-in Scripting

The deliberate absence of variables and control flow means CI/CD logic lives in the host shell, where it belongs:

```bash
#!/bin/bash
# CI assertion — uses shell variables, not tsh variables
COUNT=$(tsh test.typhon -c "count PlayerStats")
if [ "$COUNT" -lt 1000 ]; then
  echo "FAIL: Expected ≥1000 players, got $COUNT"
  exit 1
fi

# Loop — uses shell loop, not tsh loop
for DB in staging.typhon production.typhon; do
  tsh "$DB" -c "verify" || echo "INTEGRITY FAILED: $DB"
done

# Conditional backup
DIRTY=$(tsh prod.typhon -c "cache-stats" | grep "Dirty:" | awk '{print $2}')
if [ "$DIRTY" -gt 0 ]; then
  tsh prod.typhon -c "checkpoint"
fi
```

This is strictly more powerful than any embedded scripting because the host shell provides variables, loops, conditionals, pipes, subshells, parallelism, and error handling — all tested and debugged for decades.

### Exit Codes

For scripting, predictable exit codes matter. Exit codes are the universal integration contract between processes — investing in rich exit codes pays off more than investing in script-level error handling.

| Code | Meaning |
|------|---------|
| 0 | Success |
| 1 | Command error (syntax, invalid args) |
| 2 | Database error (file not found, corrupt, lock conflict) |
| 3 | Integrity check failure (`verify` found errors) |
| 10 | Unhandled exception (bug in shell) |

In batch mode (`--exec` or pipe), the exit code reflects the **first error** encountered (since execution halts on error).

## CI/CD Integration Examples

### GitHub Actions — Smoke Test

```yaml
- name: Typhon integrity check
  run: |
    tsh production.typhon -c "verify"
    tsh production.typhon -c "count PlayerStats" | grep -q "^[1-9]"
```

### Build Pipeline — Regression Test

```yaml
- name: Run data regression
  run: |
    tsh --exec test/fixtures/setup.tsh
    tsh test.typhon --format json -c "query PlayerStats order by PlayerId" > actual.json
    diff expected.json actual.json
```

### Automated Backup

```bash
#!/bin/bash
# Daily backup script
DATE=$(date +%Y%m%d)
tsh production.typhon -c "backup backups/production-${DATE}.typhon"
tsh production.typhon -c "compact"
```

## Shell Settings

### `set` Command

```
tsh> set
  Current settings:
    format:       table         (table | full-table | json | csv)
    auto-commit:  off           (on | off)
    verbose:      off           (on | off — shows stack traces on error)
    page-size:    20            (rows per page for query results)
    color:        auto          (auto | on | off)
    timing:       off           (on | off — shows execution time per command)

tsh> set timing on
tsh:mydb> count PlayerStats
  1,024
  (0.12 ms)

tsh> set format json
```

### `timing` — Performance Measurement

When timing is on, every command shows execution time. This is useful for performance investigation:

```
tsh:mydb> set timing on
tsh:mydb> query PlayerStats where Score > 90
  Entity 1 | PlayerId=1  Health=95.0  Score=15
  (1 result, 0.34 ms)

tsh:mydb> create PlayerStats { PlayerId=999, Health=100, Score=0 }
  Entity 999 created (0.08 ms)
```

## Technology Choices

### CLI Framework & Output — Spectre.Console + Spectre.Console.Cli (Decided)

| Layer | Library | Responsibility |
|-------|---------|----------------|
| **CLI surface** | Spectre.Console.Cli | Parses `tsh` command-line arguments (`--exec`, `-c`, `--format`, `--schema`). Defines commands, options, validation, and help generation. |
| **Terminal output** | Spectre.Console | Renders tables, colored text, progress bars, tree views. Powers all diagnostic and query output. |
| **REPL command parsing** | Hand-rolled | Parses inner REPL commands (`create PlayerStats { ... }`, `query ... where ...`). Domain-specific syntax that no generic framework handles well. |

Spectre.Console.Cli handles the **outer** layer (process arguments → which mode to run), while the hand-rolled parser handles the **inner** layer (REPL input → command dispatch). This split avoids forcing either tool into a role it wasn't designed for.

### REPL Line Editing — PrettyPrompt (Decided)

**Decision:** Use [PrettyPrompt](https://github.com/waf/PrettyPrompt) (`NuGet: PrettyPrompt`, v4.1.1, .NET 6+, MPL-2.0) for the interactive REPL loop.

PrettyPrompt is a `Console.ReadLine` replacement extracted from [CSharpRepl](https://github.com/waf/CSharpRepl) (3.3K ⭐). It provides syntax highlighting, autocompletion with documentation tooltips, persistent history with filtering, multi-line input with word-wrap, and cross-platform clipboard support. Its single dependency is `TextCopy` (clipboard).

**Why PrettyPrompt over alternatives:**

| Library | Status | Verdict |
|---------|--------|---------|
| **[PrettyPrompt](https://github.com/waf/PrettyPrompt)** | 195 ⭐, .NET 6+, v4.1.1 (Sep 2023) | **Selected** — feature-rich, battle-tested, callback-driven customization |
| [ReadLine](https://github.com/tonerdo/readline) | 825 ⭐, last commit **2017** | Abandoned. netstandard1.3, no syntax highlighting, no multi-line |
| [ReadLine.Ext](https://github.com/rafntor/readline.ext) | 5 ⭐, net7.0, Sep 2023 | Custom `IConsole` is nice but minimal community, no highlighting |
| [InteractiveReadLine](https://github.com/mattj23/InteractiveReadLine) | 6 ⭐, last commit **2020** | Good architecture (delegate composition) but abandoned |
| Console.ReadLine | Built-in | No history, no completion, no editing — unusable for a REPL |

**Integration with `tsh`:**

PrettyPrompt's `IPromptCallbacks` interface is the customization point. `tsh` provides callbacks for:

| Callback | `tsh` Implementation |
|----------|---------------------|
| **Completions** | Command names at start of line → component names after `create`/`query`/`describe` → field names inside `{ }` → entity IDs from recent operations |
| **Syntax highlighting** | Keywords (`begin`, `commit`, `query`, `where`) in one color, component names in another, string literals in a third, numeric values distinct |
| **History filtering** | Persistent across sessions (`~/.tsh_history`), filterable by prefix typing |

**Layer separation:** PrettyPrompt owns the *input line* (prompt, editing, completions, highlighting). Spectre.Console owns *everything below* (tables, diagnostics, progress bars). They don't conflict — PrettyPrompt yields control after the user presses Enter, then Spectre.Console renders the output.

## Project Structure

```
test/Typhon.Shell/                 # or src/Typhon.Shell/ — TBD
├── Typhon.Shell.csproj
├── Program.cs                     # Entry point, arg parsing, REPL loop
├── Commands/
│   ├── ICommand.cs                # Command interface
│   ├── DatabaseCommands.cs        # open, close, info
│   ├── SchemaCommands.cs          # load-schema, schema, describe
│   ├── TransactionCommands.cs     # begin, commit, rollback
│   ├── DataCommands.cs            # create, read, update, delete, query
│   ├── DiagnosticCommands.cs      # cache-stats, segments, btree, etc.
│   ├── AdminCommands.cs           # verify, compact, backup, checkpoint
│   └── ShellCommands.cs           # help, set, history, exit
├── Parsing/
│   ├── CommandParser.cs           # Input → command + args
│   ├── BraceExpressionParser.cs   # { Field=Value, ... } parser
│   ├── ScriptParser.cs            # .tsh file reader (line-oriented + directives)
│   └── BlockDirectiveParser.cs    # @compact/@json/@end block handler
├── Formatting/
│   ├── IOutputFormatter.cs        # Formatter interface
│   ├── TableFormatter.cs          # Default tabular output
│   ├── JsonFormatter.cs           # JSON output
│   └── CsvFormatter.cs            # CSV output
├── Schema/
│   ├── AssemblySchemaLoader.cs    # Load components from DLL
│   └── BuiltInComponents.cs       # Test components shipped with shell
└── Session/
    ├── ShellSession.cs            # Session state (db, tx, settings)
    └── PromptBuilder.cs           # Dynamic prompt generation
```

## Decisions

- [x] **No script variables** — Variables (`$count = count CompA`) are not supported. CI/CD pipelines use host shell variables (`COUNT=$(tsh ... -c "count CompA")`), which are strictly more powerful.
- [x] **No script control flow** — No `if`, `foreach`, or any control flow constructs. The host shell provides loops, conditionals, and error handling. This avoids Greenspun's tenth rule: every custom DSL converges toward a bad reimplementation of a real language.
- [x] **Script format = line-oriented + data block directives** — One command per line (sqlite3 model), with `@compact`/`@json`/`@end` as the only structural extension for bulk data ergonomics. Block directives are parser mode switches, not programming constructs.
- [x] **REPL line editing: [PrettyPrompt](https://github.com/waf/PrettyPrompt)** — Syntax highlighting, autocompletion, persistent history, multi-line input. Customized via `IPromptCallbacks`. Chosen over `ReadLine` (abandoned 2017), `ReadLine.Ext` (minimal community), `InteractiveReadLine` (abandoned 2020).

## Open Questions

- [ ] Should `Typhon.Shell` live in `test/` (dev tool) or `src/` (first-class product)?
- [ ] NuGet package distribution? Global tool (`dotnet tool install -g typhon-shell`)?
- [ ] Should `tsh` be able to create a new empty database (`open --create newdb.typhon`)?
