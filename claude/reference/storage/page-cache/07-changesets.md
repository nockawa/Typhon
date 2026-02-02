# ChangeSets and Dirty Page Tracking

This document describes how Typhon tracks modified pages and batches disk writes for efficiency.

## Overview

A `ChangeSet` accumulates modified pages during a logical operation (e.g., a transaction) and then flushes them to disk in an optimized batch:

```
┌─────────────────┐     ┌─────────────────┐     ┌─────────────────┐
│  Modify Page A  │────►│  ChangeSet.Add  │────►│  Track Dirty    │
│  Modify Page B  │────►│  ChangeSet.Add  │────►│  Track Dirty    │
│  Modify Page C  │────►│  ChangeSet.Add  │────►│  Track Dirty    │
└─────────────────┘     └─────────────────┘     └─────────────────┘
                                                        │
                                                        ▼
                                               ┌─────────────────┐
                                               │ SaveChangesAsync│
                                               │  - Sort pages   │
                                               │  - Group contig │
                                               │  - Batch I/O    │
                                               └─────────────────┘
```

## ChangeSet Class

```csharp
public class ChangeSet
{
    private readonly PagedMMF _owner;
    private readonly HashSet<int> _changedMemoryPageIndices;
    private Task _saveTask;
    
    public ChangeSet(PagedMMF owner)
    {
        _owner = owner;
        _changedMemoryPageIndices = new HashSet<int>();
    }
}
```

### Adding Pages

```csharp
public void Add(PageAccessor accessor)
{
    // Only increment dirty counter once per page per ChangeSet
    if (_changedMemoryPageIndices.Add(accessor.MemPageIndex))
    {
        _owner.IncrementDirty(accessor.MemPageIndex);
    }
}
```

**Key Point**: The `HashSet` ensures each page is tracked only once, even if modified multiple times during the operation.

### Saving Changes

```csharp
public Task SaveChangesAsync()
{
    if (_changedMemoryPageIndices.Count == 0)
        return Task.CompletedTask;
    
    // Snapshot and clear the set
    var pages = _changedMemoryPageIndices.ToArray();
    _changedMemoryPageIndices.Clear();
    
    // Delegate to PagedMMF for optimized write
    _saveTask = _owner.SavePages(pages);
    return _saveTask;
}

public void SaveChanges()
{
    SaveChangesAsync().GetAwaiter().GetResult();
}
```

### Reset (Discard Changes)

```csharp
public void Reset()
{
    // Decrement dirty counters without writing
    foreach (var memPageIndex in _changedMemoryPageIndices)
    {
        _owner.DecrementDirty(memPageIndex);
    }
    _changedMemoryPageIndices.Clear();
}
```

## Dirty Counter Mechanism

Each `PageInfo` has a `DirtyCounter` that tracks pending write-backs:

### IncrementDirty

```csharp
internal void IncrementDirty(int memPageIndex)
{
    var pi = _memPagesInfo[memPageIndex];
    
    pi.StateSyncRoot.EnterExclusiveAccess(ref WaitContext.Null);
    try
    {
        ++pi.DirtyCounter;
    }
    finally
    {
        pi.StateSyncRoot.ExitExclusiveAccess();
    }
}
```

### DecrementDirty

```csharp
internal void DecrementDirty(int memPageIndex)
{
    var pi = _memPagesInfo[memPageIndex];
    
    pi.StateSyncRoot.EnterExclusiveAccess(ref WaitContext.Null);
    try
    {
        if (--pi.DirtyCounter == 0 && pi.PageState == PageState.IdleAndDirty)
        {
            // No more pending writes, transition to Idle
            pi.PageState = PageState.Idle;
            Interlocked.Increment(ref _metrics.FreeMemPageCount);
        }
    }
    finally
    {
        pi.StateSyncRoot.ExitExclusiveAccess();
    }
}
```

### State Interaction

```
Page modified → DirtyCounter++
                     │
                     ▼
Access released → State = IdleAndDirty (if DirtyCounter > 0)
                     │
                     ▼
Page written → DirtyCounter--
                     │
                     ▼
DirtyCounter = 0 → State = Idle (can be evicted)
```

**Important**: A page in `IdleAndDirty` state **cannot be evicted** until all dirty references are flushed.

## Batch I/O Optimization

### SavePages Algorithm

```csharp
internal Task SavePages(int[] memPageIndices)
{
    // 1. Sort pages by file page index for sequential I/O
    Array.Sort(memPageIndices, (a, b) => 
        _memPagesInfo[a].FilePageIndex - _memPagesInfo[b].FilePageIndex);
    
    // 2. Group contiguous pages
    var operations = new List<(int startMemPage, int count)>();
    int runStart = 0;
    
    for (int i = 1; i <= memPageIndices.Length; i++)
    {
        bool endOfRun = i == memPageIndices.Length;
        
        if (!endOfRun)
        {
            int prevFile = _memPagesInfo[memPageIndices[i - 1]].FilePageIndex;
            int currFile = _memPagesInfo[memPageIndices[i]].FilePageIndex;
            int prevMem = memPageIndices[i - 1];
            int currMem = memPageIndices[i];
            
            // Contiguous if both file and memory indices are adjacent
            bool contiguous = (currFile == prevFile + 1) && 
                              (currMem == prevMem + 1);
            
            if (contiguous)
                continue;
            
            endOfRun = true;
        }
        
        if (endOfRun)
        {
            operations.Add((memPageIndices[runStart], i - runStart));
            runStart = i;
        }
    }
    
    // 3. Execute all writes in parallel
    var tasks = new Task[operations.Count];
    for (int i = 0; i < operations.Count; i++)
    {
        var (startMem, count) = operations[i];
        tasks[i] = WriteContiguousPages(startMem, count);
    }
    
    // 4. Decrement dirty counters after all writes complete
    return Task.WhenAll(tasks).ContinueWith(_ =>
    {
        foreach (int memPageIndex in memPageIndices)
        {
            DecrementDirty(memPageIndex);
        }
    });
}
```

### WriteContiguousPages

```csharp
private async Task WriteContiguousPages(int startMemPageIndex, int count)
{
    int filePageIndex = _memPagesInfo[startMemPageIndex].FilePageIndex;
    long fileOffset = (long)filePageIndex * PageSize;
    int totalBytes = count * PageSize;
    
    // Single write for all contiguous pages
    var memory = MemPages.AsMemory(startMemPageIndex * PageSize, totalBytes);
    
    await RandomAccess.WriteAsync(_fileHandle, memory, fileOffset);
    
    // Update metrics
    Interlocked.Add(ref _metrics.PageWrittenToDiskCount, count);
    Interlocked.Increment(ref _metrics.WrittenOperationCount);
}
```

### I/O Batching Visualization

```
Without batching (5 separate writes):
  Write Page 10 ────► Disk
  Write Page 11 ────► Disk
  Write Page 12 ────► Disk
  Write Page 20 ────► Disk
  Write Page 21 ────► Disk

With batching (2 writes):
  Write Pages 10-12 ────► Disk (single 24KB write)
  Write Pages 20-21 ────► Disk (single 16KB write)
```

**Performance Impact**:
- Reduces syscall overhead
- Enables disk controller write combining
- Better utilizes disk sequential write speed

## Multiple ChangeSets

Multiple ChangeSets can reference the same page:

```csharp
var cs1 = new ChangeSet(pagedMMF);
var cs2 = new ChangeSet(pagedMMF);

// Both modify page 5
cs1.Add(page5Accessor);  // DirtyCounter = 1
cs2.Add(page5Accessor);  // DirtyCounter = 2

// Flush cs1
await cs1.SaveChangesAsync();  // DirtyCounter = 1 (still dirty!)

// Page stays in IdleAndDirty until cs2 flushes
await cs2.SaveChangesAsync();  // DirtyCounter = 0, State = Idle
```

This ensures a page isn't evicted while any ChangeSet still references it.

## Usage Patterns

### Pattern 1: Simple Modification

```csharp
var changeSet = new ChangeSet(pagedMMF);

pagedMMF.RequestPage(pageIndex, exclusive: true, out var accessor);
using (accessor)
{
    // Modify data
    accessor.PageRawData[0] = 42;
    changeSet.Add(accessor);
}

await changeSet.SaveChangesAsync();
```

### Pattern 2: Transaction-Style

```csharp
var changeSet = new ChangeSet(pagedMMF);

try
{
    // Multiple modifications
    ModifyPage1(changeSet);
    ModifyPage2(changeSet);
    ModifyPage3(changeSet);
    
    // Commit all changes
    await changeSet.SaveChangesAsync();
}
catch
{
    // Discard on error
    changeSet.Reset();
    throw;
}
```

### Pattern 3: With ChunkAccessor

```csharp
var changeSet = new ChangeSet(pagedMMF);

using (var accessor = new ChunkAccessor(segment, changeSet))
{
    // Modifications automatically tracked via dirty flag
    byte* ptr = accessor.GetChunkAddress(chunkId, dirty: true);
    ref MyStruct data = ref Unsafe.AsRef<MyStruct>(ptr);
    data.Value = 42;
    
    // ChunkAccessor.Dispose() flushes dirty pages to ChangeSet
}

await changeSet.SaveChangesAsync();
```

### Pattern 4: Segment Operations

```csharp
// Segment methods accept optional ChangeSet
segment.AllocateChunk(clearContent: true, changeSet);
segment.Grow(newLength, clearNewPages: true, changeSet);

// Batch flush after all operations
await changeSet.SaveChangesAsync();
```

## Async I/O Details

### File Handle Configuration

```csharp
_fileHandle = File.OpenHandle(
    filePath,
    FileMode.OpenOrCreate,
    FileAccess.ReadWrite,
    FileShare.None,
    FileOptions.Asynchronous | FileOptions.RandomAccess
);
```

- **Asynchronous**: Enables overlapped I/O
- **RandomAccess**: Optimizes for non-sequential access patterns
- **FileShare.None**: Exclusive access to file

### Write Implementation

```csharp
// Uses RandomAccess API for efficient positioned writes
await RandomAccess.WriteAsync(
    _fileHandle,
    buffer,           // Memory<byte> from pinned cache
    fileOffset,       // Exact file position
    cancellationToken
);
```

**Advantages of RandomAccess**:
- No file position state to manage
- Thread-safe (no seek + write race)
- Optimal for random access patterns

## Metrics

```csharp
internal class Metrics
{
    public int PageWrittenToDiskCount;    // Total pages written
    public int WrittenOperationCount;     // Number of write syscalls
    // ...
}

// Efficiency ratio
float pagesPerWrite = (float)PageWrittenToDiskCount / WrittenOperationCount;
// Higher is better (more batching)
```

## Summary

| Aspect | Description |
|--------|-------------|
| **Tracking** | HashSet ensures each page tracked once |
| **Dirty Counter** | Reference count for pending writes |
| **State Protection** | IdleAndDirty prevents eviction |
| **Batching** | Sort + group contiguous pages |
| **I/O** | Async RandomAccess for positioned writes |
| **Multiple ChangeSets** | Each increments/decrements independently |
