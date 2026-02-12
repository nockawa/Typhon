# Typhon.Analyzers

Custom Roslyn analyzers for the Typhon database engine project.

## Analyzer Summary

| ID | Severity | Description |
|----|----------|-------------|
| TYPHON001 | Error | EpochChunkAccessor parameters must use `ref` modifier |
| TYPHON003 | Error | EpochChunkAccessor must not be copied |
| TYPHON004 | Error | IDisposable result must be disposed |

---

## DisposableNotDisposedAnalyzer (TYPHON004)

**Severity:** Error

**Description:** Detects IDisposable instances returned from method calls that are not properly disposed.

### Why This Rule Exists

Failing to dispose IDisposable resources causes resource leaks. In Typhon, this is especially critical:

| Type | Consequence if not disposed |
|------|----------------------------|
| `EpochChunkAccessor` | **Page cache deadlock** - pages remain in Shared state indefinitely |
| `Transaction` | **Data corruption** - uncommitted changes leak, resource exhaustion |

This analyzer addresses limitations in CA2000 which lacks inter-procedural analysis, misses tuple returns, and ignores exception flow paths.

### Detected Patterns

```csharp
// ERROR TYPHON004 - Discarded result
CreateTransaction();

// ERROR TYPHON004 - Explicitly discarded
_ = CreateTransaction();

// ERROR TYPHON004 - Variable never disposed
var t = CreateTransaction();
t.DoWork();
// end of method without dispose

// ERROR TYPHON004 - Reassignment without disposing first
var t = CreateTransaction();
t = CreateTransaction();  // First value leaked!
t.Dispose();
```

### Correct Usage

```csharp
// CORRECT - Using declaration (preferred)
using var t = CreateTransaction();
t.DoWork();
// Automatically disposed at end of scope

// CORRECT - Using statement
using (var t = CreateTransaction())
{
    t.DoWork();
}

// CORRECT - Explicit Dispose() call
var t = CreateTransaction();
try
{
    t.DoWork();
}
finally
{
    t.Dispose();
}

// CORRECT - Return transfers ownership to caller
public Transaction CreateAndReturn()
{
    return CreateTransaction();
}

// CORRECT - Field assignment transfers ownership
private Transaction _transaction;
public void Initialize()
{
    _transaction = CreateTransaction();
}
```

### Code Fix

The analyzer includes automatic code fixes. In Visual Studio or Rider:
1. Place cursor on the error
2. Press `Ctrl+.` (or `Alt+Enter` in Rider)
3. Select one of:
   - **"Add 'using' declaration"** - Converts `var x = Method();` to `using var x = Method();`
   - **"Add Dispose() call"** - Adds `x.Dispose();` at end of the containing block

---

## ChunkAccessorRefAnalyzer (TYPHON001)

**Severity:** Error

**Description:** Enforces that `EpochChunkAccessor` parameters must always be passed by `ref` (not `in`, not by value).

### Why This Rule Exists

`EpochChunkAccessor` is a large struct (~1KB in size) with mutating methods, designed for zero-allocation, stack-based chunk access with SIMD-optimized operations. It must be passed by `ref` only:

- **Performance Impact (by value):** Each by-value pass copies ~1KB of data onto the stack
- **Performance Impact (in):** The `in` modifier causes **defensive copies** when calling non-readonly methods on the struct, completely defeating the performance design
- **Design Violation:** EpochChunkAccessor has mutating methods like `GetChunk()` - using `in` triggers hidden copies on every call
- **Cache Pollution:** Large stack copies can pollute CPU caches and degrade performance

### Correct Usage

```csharp
// CORRECT - Pass by ref
public void ProcessChunk(ref EpochChunkAccessor accessor)
{
    ref var chunk = ref accessor.GetChunk<MyData>(chunkId);
    // No defensive copies, direct access to the struct
}

// ALSO CORRECT - Even for readonly operations
public bool ReadChunk(ref EpochChunkAccessor accessor, int chunkId)
{
    ref readonly var chunk = ref accessor.GetChunkReadOnly<MyData>(chunkId);
    return true;
}
```

### Incorrect Usage

```csharp
// ERROR TYPHON001 - Missing ref modifier
public void ProcessChunk(EpochChunkAccessor accessor)
{
    // This creates a 1KB stack copy - expensive!
}

// ERROR TYPHON001 - Using 'in' causes defensive copies
public void ProcessChunk(in EpochChunkAccessor accessor)
{
    // Every call to accessor.GetChunk() creates a defensive copy
    // because GetChunk() is not a readonly method!
    ref var chunk = ref accessor.GetChunk<MyData>(chunkId); // Hidden copy here!
}
```

### Code Fix

The analyzer includes an automatic code fix that adds or replaces with the `ref` modifier. In Visual Studio or Rider:
1. Place cursor on the error
2. Press `Ctrl+.` (or `Alt+Enter` in Rider)
3. Select "Add 'ref' modifier" or "Replace 'in' with 'ref'"

---

## ChunkAccessorCopyAnalyzer (TYPHON003)

**Severity:** Error

**Description:** Detects copying of EpochChunkAccessor instances, which defeats its zero-allocation design and creates unexpected behavior due to duplicated internal state.

### Why This Rule Exists

EpochChunkAccessor is a large ~1KB struct designed for zero-allocation. Copying it:
- Creates expensive ~1KB stack copies
- Duplicates internal state (cache, pins, etc.)
- Can lead to double-dispose or inconsistent state

The only valid creation is via `ChunkBasedSegment.CreateEpochChunkAccessor()`.

### Incorrect Usage

```csharp
// ERROR TYPHON003 - Copying via assignment
var copy = existingAccessor;

// ERROR TYPHON003 - Copying via return
return existingAccessor;
```

### Correct Usage

```csharp
// CORRECT - Create new accessor
using var accessor = segment.CreateEpochChunkAccessor();

// CORRECT - Pass by ref
ProcessData(ref accessor);
```

---

## Adding to Other Projects

To enable these analyzers in additional projects, add to the `.csproj` file:

```xml
<ItemGroup>
  <!-- Reference the Roslyn analyzers for Typhon enforcement -->
  <ProjectReference Include="path\to\Typhon.Analyzers\Typhon.Analyzers.csproj"
                    ReferenceOutputAssembly="false"
                    OutputItemType="Analyzer" />
</ItemGroup>
```

## Technical Details

- **Target Framework:** netstandard2.0 (compatible with all modern .NET versions)
- **Dependencies:**
  - Microsoft.CodeAnalysis.CSharp 5.0.0
  - Microsoft.CodeAnalysis.CSharp.Workspaces 5.0.0
  - Microsoft.CodeAnalysis.Analyzers 3.11.0
