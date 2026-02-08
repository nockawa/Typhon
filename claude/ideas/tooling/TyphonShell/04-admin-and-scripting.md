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

The base `.tsh` script format deliberately follows the **sqlite3 model**: one command per line, composable with Unix pipes and shell scripts. Three approaches were evaluated for scripting support:

1. **Custom DSL** (hand-built variables, `if`/`foreach`): Rejected — building a bespoke scripting language invites Greenspun's tenth rule: every custom DSL converges toward an ad-hoc, bug-ridden reimplementation of half a real language. Variable systems need type coercion, conditionals need scoping, loops need break/continue, and soon you're maintaining a language instead of a database tool.

2. **External host language** (embedded Lua/Python via native bindings): Rejected — native dependencies (KeraLua, IronPython) break NuGet global tool deployment, and the syntax gap between the host language and tsh commands creates a "two languages in one file" problem.

3. **Embedded scripting engine** (Scriban): **Accepted as post-v1 enhancement.** Scriban provides variables, loops, conditionals, and expressions with zero native dependencies. Its `ScriptOnly` syntax blends with tsh command keywords, and its sandboxing model controls what scripts can access. See [Future: Advanced Scripting via Scriban](#future-advanced-scripting-via-scriban) below.

The base `.tsh` format's **one concession** to pure line-orientation is **data block directives** (`@compact`, `@json`). These are justified because they solve a data ergonomics problem — not a control flow problem. They are parser mode switches ("the next N lines follow grammar X") rather than programming constructs.

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

The deliberate absence of variables and control flow in `.tsh` scripts means CI/CD logic lives in the host shell, where it belongs:

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

### Future: Advanced Scripting via Scriban

> **Status:** Not for v1 — this is a planned enhancement once the core shell is stable and the `.tsh` line-oriented format has proven its limits in practice.

#### The Gap

The line-oriented `.tsh` format handles two ends of the complexity spectrum well:

- **Simple scripts** — data loading, integrity checks, CI smoke tests. One command per line is perfect.
- **Complex automation** — bash/PowerShell scripts that invoke `tsh -c` per command. Full language power.

But there's a **painful middle ground**: scripts that need a loop or a variable but don't warrant a full bash wrapper with per-command process spawning overhead. Examples:

```
# "Create 1000 entities with sequential IDs" — can't do this in .tsh
# "Insert entity, capture its ID, use it in the next create" — impossible
# "Run a query, assert the count, branch on result" — requires bash escape
```

Each of these is a 3-line task conceptually, but becomes a 15-line polyglot bash+tsh script with subshell overhead.

#### Why Scriban

[Scriban](https://github.com/scriban/scriban) (3.8K ⭐, BSD-2-Clause, actively maintained) is a lightweight scripting language and engine for .NET. It was evaluated against several alternatives:

| Library | Stars | Deps | Startup | Verdict |
|---------|-------|------|---------|---------|
| **[Scriban](https://github.com/scriban/scriban)** | 3.8K | **Zero** | Instant (interpreter) | **Best fit** — zero deps, clean syntax, sandboxable, `ScriptOnly` mode |
| [Lua-CSharp](https://github.com/nuskey8/Lua-CSharp) | 710 | Zero (pure C#) | Instant | Good, but Lua syntax is alien to .NET developers (1-indexed, `~=`, `..`) |
| [NLua](https://github.com/NLua/NLua) | 2.2K | **Native binaries** (KeraLua) | Instant | Native deps break NuGet global tool deployment |
| [Roslyn Scripting](https://www.nuget.org/packages/Microsoft.CodeAnalysis.CSharp.Scripting/) | Microsoft | ~15MB | **1.5s cold start** | Way too heavy — loads 28 assemblies, unacceptable for a CLI tool |
| [ExpressionEvaluator](https://github.com/codingseb/ExpressionEvaluator) | 627 | Zero | Instant | Maintenance-only, author says no new features planned |
| [Schemy](https://github.com/microsoft/schemy) | 306 | Zero | Instant | **Archived** (2021). Scheme syntax unsuitable for target audience |

Scriban wins because:

1. **Zero dependencies.** A single NuGet package with no transitive deps. Critical for a global tool.
2. **The syntax blends with tsh.** Scriban's `ScriptOnly` mode uses `if`/`end`, `for`/`end`, `func`/`end` — the same plain-English keyword style as tsh commands. A Scriban-enhanced script reads like a natural extension of `.tsh`, not a different language.
3. **Built-in sandboxing.** Scriban's `TemplateContext` controls exactly which functions and objects are accessible. `tsh` commands get registered as Scriban functions; filesystem/network access stays blocked.
4. **Custom function registration is trivial.** Every tsh command becomes a callable Scriban function.
5. **Battle-tested.** Powers [kalk](https://github.com/xoofx/kalk) (developer calculator) and dozens of other tools.

#### How It Would Work

**Dual-mode script execution:**

- `.tsh` files are parsed line-by-line as before (backward compatible, always the default)
- Files with a `#!scriban` header, or invoked with `tsh --script`, are parsed by Scriban's `ScriptOnly` mode
- All tsh commands are registered as Scriban custom functions
- Block directives (`@compact`, `@json`) remain available as Scriban functions
- The REPL stays line-oriented — Scriban scripting is for *files only*, not interactive use

**Syntax example (`.tsh` with `#!scriban` header):**

```ruby
#!scriban
open "test.typhon"
begin

for i in 1..1000
  create "SampleA" { Id: i, Value: i * 100, Score: 95.5 - i * 0.01 }
end

commit

result = count "SampleA"
if result < 1000
  echo "FAIL: expected 1000, got " + result
  exit 1
end

close
```

**What this gives us over `.tsh`:**
- **Variables** — `result = count "SampleA"` captures return values
- **Loops** — `for i in 1..1000` generates bulk data without `@compact` blocks
- **Conditionals** — `if result < 1000` for assertions
- **Expressions** — `i * 100`, `95.5 - i * 0.01` for computed values
- **Functions** — `func setup_player(id, health)` for reusable patterns

**What we deliberately DON'T expose:**
- No filesystem access (no `File.Read`, no `Directory.List`)
- No network access
- No `System` namespace exposure
- No assembly loading from scripts
- Only tsh commands + math/string builtins are available

#### Implementation Priority

This is explicitly **deferred to post-v1**. The reasons:

1. The line-oriented `.tsh` format must ship first and prove itself. If 90% of scripts work fine without variables, the Scriban layer may never be needed — and that's a good outcome.
2. Adding Scriban is purely additive. It doesn't change the `.tsh` format, the REPL, or the command model. It's a new execution path for a new file mode.
3. The integration surface is small: register tsh commands as Scriban functions, parse with `ScriptOnly` mode, run. Estimated effort: 2-3 days once the command model is stable.

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

**Integration with `tsh`:**

PrettyPrompt's `IPromptCallbacks` interface is the customization point. `tsh` provides callbacks for:

| Callback | `tsh` Implementation |
|----------|---------------------|
| **Completions** | Command names at start of line → component names after `create`/`query`/`describe` → field names inside `{ }` → entity IDs from recent operations |
| **Syntax highlighting** | Keywords (`begin`, `commit`, `query`, `where`) in one color, component names in another, string literals in a third, numeric values distinct |
| **History filtering** | Persistent across sessions (`~/.tsh_history`), filterable by prefix typing |

**Layer separation:** PrettyPrompt owns the *input line* (prompt, editing, completions, highlighting). Spectre.Console owns *output* (tables, diagnostics, progress bars). Terminal.Gui owns *interactive sessions* (resource tree explorer, full-screen TUI). All three run sequentially — PrettyPrompt yields after Enter, then either Spectre.Console renders output or Terminal.Gui launches a full-screen session via alternate screen buffer. See [Part 01](./01-core-concepts.md) for the terminal ownership model.

## Project Structure

```
src/Typhon.Shell/
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
├── Interactive/                    # Terminal.Gui full-screen sessions
│   ├── ResourceTreeView.cs        # Resource navigator (TreeView<IResource>)
│   ├── ResourceDetailPanel.cs     # Right-hand detail pane (reads IMetricSource)
│   └── InteractiveSession.cs      # Terminal.Gui init/shutdown, alternate screen buffer
├── Scripting/                     # (post-v1) Scriban integration
│   ├── ScribanScriptRunner.cs     # Scriban ScriptOnly mode executor
│   └── TshFunctionRegistry.cs     # Registers tsh commands as Scriban functions
└── Session/
    ├── ShellSession.cs            # Session state (db, tx, settings)
    └── PromptBuilder.cs           # Dynamic prompt generation
```

## Decisions

- [x] **`.tsh` script format = line-oriented + data block directives** — One command per line (sqlite3 model), with `@compact`/`@json`/`@end` as the only structural extension for bulk data ergonomics. Block directives are parser mode switches, not programming constructs. No variables or control flow in the base format.
- [x] **REPL line editing: [PrettyPrompt](https://github.com/waf/PrettyPrompt)** — Syntax highlighting, autocompletion, persistent history, multi-line input. Customized via `IPromptCallbacks`. Chosen over `ReadLine` (abandoned 2017), `ReadLine.Ext` (minimal community), `InteractiveReadLine` (abandoned 2020).
- [x] **First-class product in `src/`** — `Typhon.Shell` lives at `src/Typhon.Shell/`, not `test/`. It's a shipped artifact, not a dev-only tool. It provides the primary user interface for interacting with Typhon databases outside of the C# API.
- [x] **NuGet global tool distribution** — Published as a .NET global tool: `dotnet tool install -g typhon-shell`. This gives users `tsh` on their PATH after install. The `.csproj` uses `<PackAsTool>true</PackAsTool>` and `<ToolCommandName>tsh</ToolCommandName>`.
- [x] **Database creation via `open`** — `open` creates a new database if the file doesn't exist (like `sqlite3 newdb.sqlite`). No separate `--create` flag needed — this matches the sqlite3 convention and is the path of least surprise. If the file exists, it opens it; if not, it creates it.
- [x] **Advanced scripting (post-v1): [Scriban](https://github.com/scriban/scriban)** — Adds variables, loops, conditionals, and expressions to `.tsh` scripts via `#!scriban` header or `--script` flag. Zero dependencies, BSD-2-Clause, 3.8K ⭐. Chosen over Lua-CSharp (alien syntax for .NET devs), NLua (native deps), Roslyn Scripting (1.5s cold start, 15MB), ExpressionEvaluator (maintenance-only). See the "Future: Advanced Scripting via Scriban" section above for full rationale and design.

## Open Questions

*(None remaining — all resolved.)*
