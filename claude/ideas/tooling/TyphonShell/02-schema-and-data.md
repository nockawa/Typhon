# Part 02 — Schema & Data Operations

**Date:** 2026-02-08
**Status:** Raw idea

## The Schema Bridge Problem

This is the central design challenge. Typhon components are compiled C# structs:

```csharp
[Component]
public struct PlayerStats
{
    [Field] [Index] public int PlayerId;
    [Field] public float Health;
    [Field] public int Score;
}
```

The shell works with text. How do we go from `create PlayerStats { PlayerId=1, Health=100.0, Score=0 }` to a typed, blittable struct that the engine can store?

### Assembly Loading

The user creates a .NET class library containing their component definitions, compiles it in their IDE, and loads the resulting DLL into `tsh`. This is the standard workflow — the schema lives in code, just like any other Typhon application.

```
tsh> load-schema "MyGame.Components.dll"
  Loaded 3 components: PlayerStats, Inventory, Position

tsh> describe PlayerStats
  PlayerStats [12 bytes, blittable]
    PlayerId    Int32     [indexed, unique]
    Health      Single
    Score       Int32
```

**Loading pipeline:**

1. **Load assembly** — `Assembly.LoadFrom(path)` loads the DLL into the shell's process
2. **Discover components** — Scan for types with `[Component]` attribute
3. **Extract metadata** — For each component, read `[Field]` attributes (name, offset, type) and `[Index]` attributes (unique/multi, field type)
4. **Register with engine** — Call `DatabaseEngine.RegisterComponent<T>()` via reflection (using `MakeGenericMethod`)
5. **Build field map** — Store a `ComponentSchema` object per type: field names → (offset, size, `FieldType` enum) for runtime text-to-binary conversion

**Reload workflow:**
When the user recompiles their assembly, they should be able to reload:

```
tsh:mydb> reload-schema
  Reloading MyGame.Components.dll...
  ✓ PlayerStats: unchanged
  ✓ Inventory: unchanged
  ⚠ Position: field added (Rotation: QuaternionF) — existing data not affected
  Reload complete
```

> **Note:** Hot-reloading assemblies in .NET is non-trivial (`AssemblyLoadContext` unloading, type identity changes). This may require restarting the engine instance. Worth researching during design phase.

**Multiple assemblies:**
A user may have components split across multiple DLLs:

```
tsh> load-schema "MyGame.Core.dll"
  Loaded 2 components: PlayerStats, Inventory
tsh> load-schema "MyGame.World.dll"
  Loaded 3 components: Position, Terrain, Weather
tsh> schema
  5 components loaded from 2 assemblies
```

### Text-to-Struct Conversion

This is the core mechanism that bridges text input to Typhon's blittable structs. Given user input like `{ PlayerId=1, Health=100.0, Score=0 }`, the converter must produce the exact binary layout the engine expects.

**Conversion pipeline:**

```
Text Input → Input Parser → Field/Value pairs → Type Converter → Binary Buffer → Engine
                  ↑                                    ↑
          (format-specific)                   (FieldType-driven)
```

1. **Parse input** — An input parser (format-specific, see below) produces a list of `(fieldName, textValue)` pairs
2. **Resolve fields** — Look up each field name in the `ComponentSchema` to get offset, size, and `FieldType`
3. **Convert values** — Parse each text value according to its `FieldType` (see type table below)
4. **Write to buffer** — Allocate an unmanaged buffer of the struct's size, write each converted value at its offset
5. **Pass to engine** — Use the buffer as a `ref T` via unsafe pointer cast

**Supported field types and their text representations:**

| FieldType | C# Type | Text Example | Parse Notes |
|-----------|---------|-------------|-------------|
| Boolean | `bool` | `true`, `false` | Case-insensitive |
| Byte | `sbyte` | `42` | Signed, -128..127 |
| UByte | `byte` | `200` | Unsigned, 0..255 |
| Char | `char` | `'A'` | Single quoted character |
| Short | `short` | `-1000` | |
| UShort | `ushort` | `60000` | |
| Int | `int` | `42` | Default for unadorned integers |
| UInt | `uint` | `42u` | Suffix `u` or explicit cast |
| Float | `float` | `95.5` | Default for unadorned decimals |
| Double | `double` | `95.5d` | Suffix `d` or explicit cast |
| Long | `long` | `123456789L` | Suffix `L` for disambiguation |
| ULong | `ulong` | `42uL` | |
| String64 | `String64` | `"hello"` | Double-quoted, max 63 UTF-8 chars |
| String1024 | `String1024` | `"longer text..."` | Double-quoted, max ~1023 chars |
| VarString | `string` | `"any length"` | Double-quoted, stored in VSB segment |
| Point2F | `Point2F` | `(1.0, 2.0)` | Parenthesized tuple |
| Point3F | `Point3F` | `(1.0, 2.0, 3.0)` | |
| Point4F | `Point4F` | `(1, 2, 3, 4)` | |
| Point2D | `Point2D` | `(1.0d, 2.0d)` | Double-precision variant |
| Point3D | `Point3D` | `(1.0d, 2.0d, 3.0d)` | |
| Point4D | `Point4D` | `(1.0d, 2.0d, 3.0d, 4.0d)` | |
| QuaternionF | `QuaternionF` | `(0, 0, 0, 1)` | X, Y, Z, W |
| QuaternionD | `QuaternionD` | `(0d, 0d, 0d, 1d)` | |
| Variant | `Variant` | `variant("si:42")` | Explicit type-tagged format |

> **Type inference:** In most cases the converter knows the target type from the schema, so `Health=95` is unambiguous (the schema says `Health` is a `Float`, so `95` → `95.0f`). Explicit suffixes (`d`, `L`, `u`) are only needed when overriding or in ambiguous contexts.

### Input Formats

Different contexts have different ergonomic needs. The text-to-struct converter accepts input from pluggable **input parsers**, each producing the same `(fieldName, textValue)` pairs.

#### Brace Format (Interactive Default)

The primary format for interactive use — terse, field-name-driven:

```
tsh:mydb[tx:42]> create PlayerStats { PlayerId=1, Health=100.0, Score=0 }
```

Rules:
- Curly braces delimit the expression
- Comma-separated `Field=Value` pairs
- String values are double-quoted: `Name="Alice"`
- Tuples use parentheses: `Position=(1.0, 2.0, 3.0)`
- Whitespace is flexible

#### Compact Format (Script Optimized)

For `.tsh` script files, the brace format works but gets verbose for bulk data. A compact, positional format could be useful:

```bash
# script.tsh — bulk insert
# @compact tells the parser to use positional field order from schema
@compact PlayerStats
create 1, 100.0, 0
create 2, 85.5, 12
create 3, 92.0, 5
@end
```

Fields are matched by position (schema order: `PlayerId`, `Health`, `Score`). This is faster to write for bulk operations and easier to generate from external tools.

#### JSON Format (Pipe/CI Optimized)

For programmatic input via pipes or CI/CD:

```bash
echo '{"command":"create","component":"PlayerStats","data":{"PlayerId":1,"Health":100.0,"Score":0}}' | tsh mydb.typhon --format json
```

Or in a script file with a JSON block:

```bash
# script.tsh
@json PlayerStats
[
  { "PlayerId": 1, "Health": 100.0, "Score": 0 },
  { "PlayerId": 2, "Health": 85.5, "Score": 12 }
]
@end
```

JSON is unambiguous, machine-readable, and familiar to CI pipelines. It's the natural choice for piped input.

#### Format Summary

| Format | Best For | Verbosity | Ambiguity | Machine-Friendly |
|--------|----------|-----------|-----------|-------------------|
| **Brace** | Interactive REPL | Medium | Low (schema-resolved) | No |
| **Compact** | Bulk script data | Low | Medium (positional) | Somewhat |
| **JSON** | CI/CD, pipes, tools | High | None (self-describing) | Yes |

> **Script integration:** In `.tsh` script files, the compact and JSON formats are accessed via `@compact ComponentName` / `@json ComponentName` / `@end` block directives. These are parser mode switches — the only structural extension to the otherwise line-oriented script format. See [Part 04](./04-admin-and-scripting.md) for the full script format specification.

### Approach 2: Built-in Test Components

Ship a set of simple components directly in the `Typhon.Shell` project for immediate experimentation:

```csharp
// Built into Typhon.Shell
[Component]
public struct SampleA
{
    [Field] [Index] public int Id;
    [Field] public int Value;
    [Field] public float Score;
}

[Component]
public struct SampleB
{
    [Field] [Index] public long Key;
    [Field] public String64 Name;
}
```

Available out-of-the-box without loading any assembly. Useful for:
- Learning the engine
- Quick experiments
- CI/CD smoke tests
- Benchmarking

## Data Operations

### Transaction Control

```
tsh:mydb> begin
  Transaction started (tick 100)

tsh:mydb[tx:100]> ... data operations ...

tsh:mydb[tx:100*]> commit
  Committed: 3 creates, 1 update, 0 deletes

tsh:mydb[tx:100*]> rollback
  Rolled back (3 pending operations discarded)
```

**Auto-transaction mode** (optional setting): if no explicit `begin`, each data command runs in its own auto-committed transaction:

```
tsh:mydb> set auto-commit on
tsh:mydb> create PlayerStats { PlayerId=1, Health=100.0, Score=0 }
  Entity 1 created (auto-committed)
```

### Create

```
tsh:mydb[tx:100]> create PlayerStats { PlayerId=1, Health=100.0, Score=0 }
  Entity 1 created

# Create multiple entities
tsh:mydb[tx:100]> create PlayerStats { PlayerId=2, Health=85.5, Score=12 }
  Entity 2 created

# Multi-component entity (same entity ID, different component tables)
tsh:mydb[tx:100]> create PlayerStats { PlayerId=1, Health=100.0, Score=0 } + Inventory { Slots=20 }
  Entity 3 created (PlayerStats + Inventory)
```

### Read

```
tsh:mydb> read 1 PlayerStats
  Entity 1 | PlayerId=1  Health=100.0  Score=0

# Read all components for an entity
tsh:mydb> read 1
  Entity 1:
    PlayerStats | PlayerId=1  Health=100.0  Score=0
    Inventory   | Slots=20

# Read specific fields
tsh:mydb> read 1 PlayerStats.Health
  100.0
```

### Update

```
tsh:mydb[tx:100]> update 1 PlayerStats { Health=95.0, Score=10 }
  Entity 1 updated (rev 2)

# Increment syntax (sugar)
tsh:mydb[tx:100]> update 1 PlayerStats { Score+=5 }
  Entity 1 updated (rev 3)
```

### Delete

```
tsh:mydb[tx:100]> delete 1 PlayerStats
  Entity 1 PlayerStats deleted

# Delete entity from all component tables
tsh:mydb[tx:100]> delete 1
  Entity 1 deleted from: PlayerStats, Inventory
```

### Query

Leverages Typhon's existing query system:

```
tsh:mydb> query PlayerStats
  Entity 1 | PlayerId=1  Health=95.0   Score=15
  Entity 2 | PlayerId=2  Health=85.5   Score=12
  (2 results)

tsh:mydb> query PlayerStats where Health > 90
  Entity 1 | PlayerId=1  Health=95.0  Score=15
  (1 result)

tsh:mydb> query PlayerStats where Score > 0 order by Score desc limit 10
  Entity 1 | PlayerId=1  Health=95.0   Score=15
  Entity 2 | PlayerId=2  Health=85.5   Score=12
  (2 results)

tsh:mydb> count PlayerStats
  2

tsh:mydb> count PlayerStats where Health > 90
  1
```

### Data Display Formatting

Results are displayed in a compact table by default, with options:

```
# Default: compact table
tsh:mydb> query PlayerStats
  Entity 1 | PlayerId=1  Health=95.0   Score=15
  Entity 2 | PlayerId=2  Health=85.5   Score=12

# Full table mode
tsh:mydb> set format full-table
tsh:mydb> query PlayerStats
  ┌──────────┬──────────┬────────┬───────┐
  │ EntityId │ PlayerId │ Health │ Score │
  ├──────────┼──────────┼────────┼───────┤
  │        1 │        1 │   95.0 │    15 │
  │        2 │        2 │   85.5 │    12 │
  └──────────┴──────────┴────────┴───────┘

# JSON mode (useful for piping)
tsh:mydb> set format json
tsh:mydb> query PlayerStats
  [
    { "entityId": 1, "PlayerId": 1, "Health": 95.0, "Score": 15 },
    { "entityId": 2, "PlayerId": 2, "Health": 85.5, "Score": 12 }
  ]

# CSV mode
tsh:mydb> set format csv
tsh:mydb> query PlayerStats
  EntityId,PlayerId,Health,Score
  1,1,95.0,15
  2,2,85.5,12
```

## Open Questions

- [ ] Should `create` return the entity ID, or should the user be able to specify it?
- [ ] Multi-component create syntax: `+` operator, or separate commands?
- [ ] Should queries support joins across component tables? (Entities with both CompA and CompB)
- [ ] Pagination for large result sets: automatic or manual (`limit`/`offset`)?
- [ ] Assembly hot-reload: full engine restart or `AssemblyLoadContext` unloading? Needs research.
- [ ] Compact format: is positional-by-schema-order sufficient, or do we need named-but-terse syntax too?
- [ ] Collection and nested Component fields: how to represent in text input? (May be deferred to v2)
- [ ] Should the JSON input format support streaming (NDJSON — one JSON object per line) for large imports?
