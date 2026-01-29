# Typhon.Analyzers

Custom Roslyn analyzers for the Typhon database engine project.

## ChunkAccessorRefAnalyzer (TYPHON001)

**Severity:** Error

**Description:** Enforces that `ChunkAccessor` parameters must always be passed by `ref` (not `in`, not by value).

### Why This Rule Exists

`ChunkAccessor` is a large struct (~1KB in size) with mutating methods, designed for zero-allocation, stack-based chunk access with SIMD-optimized operations. It must be passed by `ref` only:

- **Performance Impact (by value):** Each by-value pass copies ~1KB of data onto the stack
- **Performance Impact (in):** The `in` modifier causes **defensive copies** when calling non-readonly methods on the struct, completely defeating the performance design
- **Design Violation:** ChunkAccessor has mutating methods like `GetChunk()` - using `in` triggers hidden copies on every call
- **Cache Pollution:** Large stack copies can pollute CPU caches and degrade performance

### Correct Usage

```csharp
// ✅ CORRECT - Pass by ref
public void ProcessChunk(ref ChunkAccessor accessor)
{
    ref var chunk = ref accessor.GetChunk<MyData>(chunkId);
    // No defensive copies, direct access to the struct
}

// ✅ ALSO CORRECT - Even for readonly operations
public bool ReadChunk(ref ChunkAccessor accessor, int chunkId)
{
    ref readonly var chunk = ref accessor.GetChunkReadOnly<MyData>(chunkId);
    return true;
}
```

### Incorrect Usage

```csharp
// ❌ ERROR TYPHON001 - Missing ref modifier
public void ProcessChunk(ChunkAccessor accessor)
{
    // This creates a 1KB stack copy - expensive!
}

// ❌ ERROR TYPHON001 - Using 'in' causes defensive copies
public void ProcessChunk(in ChunkAccessor accessor)
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

## Adding to Other Projects

To enable this analyzer in additional projects, add to the `.csproj` file:

```xml
<ItemGroup>
  <!-- Reference the Roslyn analyzer for ChunkAccessor enforcement -->
  <ProjectReference Include="path\to\Typhon.Analyzers\Typhon.Analyzers.csproj"
                    ReferenceOutputAssembly="false"
                    OutputItemType="Analyzer" />
</ItemGroup>
```

## Technical Details

- **Target Framework:** netstandard2.0 (compatible with all modern .NET versions)
- **Dependencies:**
  - Microsoft.CodeAnalysis.CSharp 4.8.0
  - Microsoft.CodeAnalysis.CSharp.Workspaces 4.8.0
  - Microsoft.CodeAnalysis.Analyzers 3.3.4

## Future Analyzers

This project can be extended with additional Typhon-specific analyzers:
- Component blittability validation
- Transaction disposal enforcement
- MVCC pattern verification
- And more...
