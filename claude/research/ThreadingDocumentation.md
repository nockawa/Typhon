# Threading Documentation Strategies for Typhon

**Date:** November 2024
**Status:** In progress
**Outcome:** —

---

## Overview

This document explores different approaches to make threading requirements clear in the Typhon library, both for implementers/contributors and users. The challenge is particularly important for Typhon given its microsecond-level performance targets and complex concurrency architecture involving MVCC, transactions, and lock-based synchronization.

**Key Principles:**
1. **Implicit assumptions are dangerous** - unclear thread-safety contracts lead to race conditions and hard-to-debug issues
2. **Multiple layers of defense** - combining documentation, type system, and analyzers provides the best safety
3. **Performance-critical code** like Typhon needs zero-overhead solutions for hot paths while maintaining clarity

---

## Option 1: Documentation & XML Comments

### Approach
Use standardized XML documentation tags to document threading requirements.

```csharp
/// <summary>
/// Manages component data storage with MVCC support.
/// </summary>
/// <threadsafety>
/// This type is thread-safe. All public methods can be called concurrently.
/// Internal state is protected by AccessControl locks.
/// </threadsafety>
public class ComponentTable
{
    /// <summary>
    /// Reads a component revision.
    /// </summary>
    /// <threadsafety static="true" instance="true">
    /// Thread-safe. Multiple threads can call this method concurrently.
    /// </threadsafety>
    public bool ReadComponent(long entityId, out MyComponent component) { }
}

/// <summary>
/// Provides cached access to chunk data.
/// </summary>
/// <threadsafety>
/// NOT thread-safe. Each thread must use its own ChunkAccessor instance.
/// Designed for thread-local or stack allocation patterns.
/// </threadsafety>
public ref struct ChunkAccessor
{
}
```

### Pros
- ✅ Zero runtime overhead
- ✅ Visible in IntelliSense/IDE tooltips
- ✅ Can document nuanced threading contracts
- ✅ Works with existing documentation generation (DocFx)

### Cons
- ❌ Easy to ignore or miss
- ❌ No compile-time enforcement
- ❌ Requires discipline to maintain
- ❌ Custom tags like `<threadsafety>` need DocFx templates

### Implementation in Typhon
You could extend your DocFx documentation with a custom `<threadsafety>` tag processor.

---

## Option 2: Naming Conventions

### Approach
Encode threading requirements in type/method names.

```csharp
// Thread-safe types (no suffix)
public class ComponentTable { }
public class TransactionChain { }

// Thread-local/unsafe types (explicit suffix)
public ref struct ChunkAccessorThreadLocal { }
public class PageCacheEntryUnsafe { }

// Or use prefixes for affinity
public ref struct TLS_ChunkAccessor { }  // Thread-Local Storage
public class MT_ComponentTable { }       // Multi-Threaded
public class ST_RevisionBuilder { }      // Single-Threaded
```

### Pros
- ✅ Immediately visible in code
- ✅ Zero runtime overhead
- ✅ Self-documenting
- ✅ Easy to grep/search for patterns

### Cons
- ❌ Clutters names
- ❌ No enforcement (can still misuse)
- ❌ Can be inconsistently applied
- ❌ May feel awkward for well-known patterns

### Recommendations for Typhon
- Use suffixes sparingly for truly ambiguous cases
- Consider for internal/advanced APIs where confusion is likely

---

## Option 3: Type System Enforcement

### Approach
Use separate types to enforce threading guarantees at compile time.

```csharp
// Shared (thread-safe) access
public readonly struct SharedComponentTable
{
    private readonly ComponentTable _table;

    public SharedComponentTable(ComponentTable table) => _table = table;

    // Only thread-safe operations exposed
    public bool TryRead(long entityId, out MyComponent comp) => _table.TryRead(entityId, out comp);
}

// Exclusive (single-threaded) access
public readonly struct ExclusiveComponentTable
{
    private readonly ComponentTable _table;

    public ExclusiveComponentTable(ComponentTable table) => _table = table;

    // All operations including writes
    public void Write(long entityId, ref MyComponent comp) => _table.Write(entityId, ref comp);
    public bool TryRead(long entityId, out MyComponent comp) => _table.TryRead(entityId, out comp);
}

// Core type is internal
internal class ComponentTable
{
    public SharedComponentTable AsShared() => new(this);
    public ExclusiveComponentTable AsExclusive() => new(this);
}
```

### Advanced Pattern: Capability Tokens
```csharp
public readonly ref struct ReadCapability
{
    private readonly ComponentTable _table;
    internal ReadCapability(ComponentTable table) => _table = table;

    public bool TryRead(long entityId, out MyComponent comp) => _table.UnsafeRead(entityId, out comp);
}

public readonly ref struct WriteCapability
{
    private readonly ComponentTable _table;
    internal WriteCapability(ComponentTable table) => _table = table;

    public void Write(long entityId, ref MyComponent comp) => _table.UnsafeWrite(entityId, ref comp);
    public bool TryRead(long entityId, out MyComponent comp) => _table.UnsafeRead(entityId, out comp);
}

public class ComponentTable
{
    private readonly AccessControl _lock = new();

    public ReadCapability AcquireReadAccess()
    {
        _lock.EnterShared();
        return new ReadCapability(this);
    }

    public WriteCapability AcquireWriteAccess()
    {
        _lock.EnterExclusive();
        return new WriteCapability(this);
    }

    public void Release() => _lock.Exit();
}

// Usage enforces proper locking
using (var capability = table.AcquireWriteAccess())
{
    capability.Write(entityId, ref component);
} // Auto-release
```

### Pros
- ✅ **Compile-time enforcement** - impossible to misuse
- ✅ Self-documenting through type signatures
- ✅ Zero runtime overhead (structs inline away)
- ✅ Works with generic constraints

### Cons
- ❌ API surface area explosion
- ❌ Can be complex to design correctly
- ❌ May require significant refactoring
- ❌ Wrapper types add cognitive overhead

### Applicability to Typhon
This is particularly powerful for Typhon's lock types (`AccessControl`, `SharedAccess`, `ExclusiveAccess`). You could create capability tokens that make it impossible to call methods without proper locking.

---

## Option 4: Custom Attributes

### Approach
Define attributes to mark threading requirements, consumable by analyzers.

```csharp
namespace Typhon.Engine.Annotations
{
    /// <summary>
    /// Indicates that a type or member is thread-safe.
    /// </summary>
    [AttributeUsage(AttributeTargets.Class | AttributeTargets.Struct | AttributeTargets.Method | AttributeTargets.Property)]
    public sealed class ThreadSafeAttribute : Attribute
    {
        /// <summary>
        /// Gets whether all instance members are thread-safe.
        /// </summary>
        public bool AllMembers { get; set; } = true;
    }

    /// <summary>
    /// Indicates that a type or member requires external synchronization.
    /// </summary>
    [AttributeUsage(AttributeTargets.Class | AttributeTargets.Struct | AttributeTargets.Method | AttributeTargets.Property)]
    public sealed class RequiresSynchronizationAttribute : Attribute
    {
        public string? Reason { get; set; }
    }

    /// <summary>
    /// Indicates that a type is designed for single-threaded use only.
    /// </summary>
    [AttributeUsage(AttributeTargets.Class | AttributeTargets.Struct)]
    public sealed class SingleThreadedAttribute : Attribute
    {
        public string? Reason { get; set; }
    }

    /// <summary>
    /// Indicates that a type should be used with thread-local storage.
    /// </summary>
    [AttributeUsage(AttributeTargets.Class | AttributeTargets.Struct)]
    public sealed class ThreadLocalAttribute : Attribute { }

    /// <summary>
    /// Indicates the lock that must be held when calling this member.
    /// </summary>
    [AttributeUsage(AttributeTargets.Method | AttributeTargets.Property)]
    public sealed class RequiresLockAttribute : Attribute
    {
        public string LockFieldName { get; }
        public LockMode Mode { get; }

        public RequiresLockAttribute(string lockFieldName, LockMode mode = LockMode.Exclusive)
        {
            LockFieldName = lockFieldName;
            Mode = mode;
        }
    }

    public enum LockMode
    {
        Shared,
        Exclusive
    }

    /// <summary>
    /// Indicates that a type is immutable after construction.
    /// </summary>
    [AttributeUsage(AttributeTargets.Class | AttributeTargets.Struct)]
    public sealed class ImmutableAttribute : Attribute { }
}
```

### Usage Examples
```csharp
[ThreadSafe(AllMembers = true)]
public class ComponentTable
{
    private readonly AccessControl _lock = new();

    [RequiresLock(nameof(_lock), LockMode.Shared)]
    private bool UnsafeRead(long entityId, out MyComponent comp) { }

    [RequiresLock(nameof(_lock), LockMode.Exclusive)]
    private void UnsafeWrite(long entityId, ref MyComponent comp) { }

    [ThreadSafe] // Public API handles locking internally
    public bool Read(long entityId, out MyComponent comp)
    {
        _lock.EnterShared();
        try { return UnsafeRead(entityId, out comp); }
        finally { _lock.ExitShared(); }
    }
}

[SingleThreaded(Reason = "Designed for stack allocation and cache locality")]
public ref struct ChunkAccessor
{
    private Span<byte> _cache;
}

[ThreadLocal]
public class TransactionCache
{
    // Should be used as [ThreadStatic] or ThreadLocal<T>
}

[Immutable] // Immutable types are always thread-safe for reading
public readonly struct ComponentRevision
{
    public readonly long ChunkId;
    public readonly DateTime Timestamp;
}
```

### Pros
- ✅ Explicit contract in code
- ✅ Can be consumed by analyzers
- ✅ Visible in metadata/reflection
- ✅ Zero runtime overhead
- ✅ Standardizable across projects

### Cons
- ❌ Requires analyzer to be useful
- ❌ No enforcement without tooling
- ❌ Attributes can become verbose
- ❌ Need team agreement on conventions

---

## Option 5: Roslyn Analyzers ⭐ **Recommended**

### Approach
Create custom Roslyn analyzers that enforce threading requirements based on attributes or patterns.

### Analyzer Capabilities

#### Analyzer 1: Thread-Safety Contract Enforcement
```csharp
// Rule: TYPHON001 - Calling RequiresSynchronization member without proper locking
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public class ThreadSafetyAnalyzer : DiagnosticAnalyzer
{
    public const string DiagnosticId = "TYPHON001";

    private static readonly DiagnosticDescriptor Rule = new(
        DiagnosticId,
        "Member requires synchronization",
        "Member '{0}' requires holding lock '{1}' in {2} mode",
        "Threading",
        DiagnosticSeverity.Error,
        isEnabledByDefault: true);

    // Detects:
    // - Calling [RequiresLock] methods without acquiring lock
    // - Acquiring wrong lock mode (shared vs exclusive)
    // - Lock not held in containing scope
}
```

**Example Error:**
```csharp
private readonly AccessControl _lock = new();

[RequiresLock(nameof(_lock), LockMode.Exclusive)]
private void UnsafeWrite(long id, ref MyComponent c) { }

public void BadUsage()
{
    UnsafeWrite(123, ref comp); // ❌ ERROR TYPHON001: Member 'UnsafeWrite' requires holding lock '_lock' in Exclusive mode
}

public void GoodUsage()
{
    _lock.EnterExclusive();
    try
    {
        UnsafeWrite(123, ref comp); // ✅ OK
    }
    finally { _lock.ExitExclusive(); }
}
```

#### Analyzer 2: Thread-Local Type Validation
```csharp
// Rule: TYPHON002 - Thread-local type used across threads
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public class ThreadLocalAnalyzer : DiagnosticAnalyzer
{
    public const string DiagnosticId = "TYPHON002";

    // Detects:
    // - [ThreadLocal] types stored in instance fields
    // - [ThreadLocal] types passed to other threads (Task.Run, Thread.Start, etc.)
    // - [SingleThreaded] types used in concurrent collections
}
```

**Example Error:**
```csharp
[ThreadLocal]
public class TransactionCache { }

public class DatabaseEngine
{
    private TransactionCache _cache; // ❌ ERROR TYPHON002: ThreadLocal type should not be stored in instance field

    [ThreadStatic]
    private static TransactionCache _threadCache; // ✅ OK
}

public void ShareAcrossThreads()
{
    var cache = new TransactionCache();
    Task.Run(() => cache.DoWork()); // ❌ ERROR TYPHON002: ThreadLocal type captured in async delegate
}
```

#### Analyzer 3: Ref Struct Thread-Safety
```csharp
// Rule: TYPHON003 - Ref struct potentially shared across threads
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public class RefStructThreadingAnalyzer : DiagnosticAnalyzer
{
    // Detects:
    // - ref struct captured in closures/lambdas (impossible, but good error message)
    // - ref struct stored in fields
    // - ref struct used with async/await
}
```

#### Analyzer 4: Immutability Validation
```csharp
// Rule: TYPHON004 - Type marked [Immutable] has mutable fields
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public class ImmutabilityAnalyzer : DiagnosticAnalyzer
{
    // Detects:
    // - [Immutable] types with non-readonly fields
    // - [Immutable] types with mutable reference types
    // - [Immutable] types with setters
}
```

**Example Error:**
```csharp
[Immutable]
public struct ComponentRevision
{
    public long ChunkId; // ❌ ERROR TYPHON004: Immutable type has mutable field 'ChunkId'
    public readonly DateTime Timestamp; // ✅ OK
}

[Immutable]
public readonly struct ComponentRevisionFixed // ✅ OK
{
    public readonly long ChunkId;
    public readonly DateTime Timestamp;
}
```

#### Analyzer 5: Pattern-Based Detection
```csharp
// Rule: TYPHON005 - Potential race condition detected
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public class RaceConditionAnalyzer : DiagnosticAnalyzer
{
    // Detects common anti-patterns:
    // - Check-then-act without lock held continuously
    // - Double-checked locking bugs
    // - Unsafe publication patterns
}
```

**Example Warning:**
```csharp
private Dictionary<long, Component> _cache = new();
private readonly object _lock = new();

public Component GetOrAdd(long id)
{
    if (_cache.ContainsKey(id)) // ⚠️ WARNING TYPHON005: Check-then-act race condition
        return _cache[id];

    lock (_lock)
    {
        if (!_cache.ContainsKey(id))
            _cache[id] = new Component();
        return _cache[id];
    }
}

// Better:
public Component GetOrAddFixed(long id)
{
    lock (_lock)
    {
        if (!_cache.TryGetValue(id, out var comp))
        {
            comp = new Component();
            _cache[id] = comp;
        }
        return comp; // ✅ OK
    }
}
```

### Implementation Structure

```
Typhon.Analyzers/
├── Typhon.Analyzers/                    # Analyzer project
│   ├── ThreadSafetyAnalyzer.cs
│   ├── ThreadLocalAnalyzer.cs
│   ├── ImmutabilityAnalyzer.cs
│   ├── RaceConditionAnalyzer.cs
│   └── Typhon.Analyzers.csproj
├── Typhon.Analyzers.CodeFixes/          # Code fix providers
│   ├── AddThreadStaticCodeFixProvider.cs
│   ├── AddLockingCodeFixProvider.cs
│   └── Typhon.Analyzers.CodeFixes.csproj
├── Typhon.Analyzers.Tests/              # Analyzer tests
│   └── Typhon.Analyzers.Tests.csproj
└── Typhon.Analyzers.Package/            # NuGet packaging
    └── Typhon.Analyzers.Package.csproj
```

### Integration
```xml
<!-- Typhon.Engine.csproj -->
<ItemGroup>
  <PackageReference Include="Typhon.Analyzers" Version="1.0.0" PrivateAssets="all" />
</ItemGroup>
```

### Pros
- ✅ **Compile-time enforcement with great diagnostics**
- ✅ Automatic code fixes can suggest corrections
- ✅ Works across entire solution
- ✅ Can detect complex patterns (flow analysis)
- ✅ Integrates with CI/CD (treat warnings as errors)
- ✅ Provides IntelliSense guidance
- ✅ Can be distributed as NuGet package

### Cons
- ❌ Significant development effort
- ❌ Requires Roslyn API knowledge
- ❌ Can have false positives
- ❌ Performance impact during compilation (usually minor)
- ❌ Needs maintenance as C# evolves

---

## Option 6: Separate Assemblies/Namespaces

### Approach
Physically separate thread-safe from thread-local APIs.

```csharp
// Typhon.Engine.ThreadSafe.dll
namespace Typhon.Engine.ThreadSafe
{
    public class ComponentTable { } // All thread-safe APIs
    public class TransactionChain { }
}

// Typhon.Engine.Unsafe.dll
namespace Typhon.Engine.Unsafe
{
    public ref struct ChunkAccessor { } // All unsafe/thread-local APIs
    public class DirectPageAccess { }
}

// Typhon.Engine.Core.dll
namespace Typhon.Engine
{
    // Shared types (immutable, enums, attributes)
}
```

### Pros
- ✅ Clear separation at architectural level
- ✅ Can control visibility/access
- ✅ Easy to document whole assembly
- ✅ InternalsVisibleTo can hide internals

### Cons
- ❌ Organizational overhead
- ❌ May not align with logical grouping
- ❌ Deployment complexity
- ❌ Doesn't prevent misuse once referenced

---

## Option 7: Runtime Assertions (Debug Only)

### Approach
Add runtime checks that validate threading assumptions.

```csharp
[SingleThreaded]
public ref struct ChunkAccessor
{
#if DEBUG
    private readonly int _creationThreadId = Environment.CurrentManagedThreadId;

    private void AssertSameThread()
    {
        Debug.Assert(_creationThreadId == Environment.CurrentManagedThreadId,
            "ChunkAccessor accessed from wrong thread. " +
            $"Created on thread {_creationThreadId}, accessed on {Environment.CurrentManagedThreadId}");
    }
#else
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void AssertSameThread() { }
#endif

    public Span<byte> GetChunk(int index)
    {
        AssertSameThread();
        // ... implementation
    }
}
```

### Advanced: Lock Order Validation
```csharp
public class AccessControl
{
    private static readonly ThreadLocal<Stack<int>> _lockOrder = new(() => new Stack<int>());
    private readonly int _lockId = Interlocked.Increment(ref _nextLockId);
    private static int _nextLockId;

    public void EnterExclusive()
    {
#if DEBUG
        var stack = _lockOrder.Value!;
        if (stack.Count > 0 && stack.Peek() >= _lockId)
            throw new InvalidOperationException(
                $"Lock order violation: attempting to acquire lock {_lockId} while holding {stack.Peek()}");
        stack.Push(_lockId);
#endif
        // ... actual locking
    }

    public void ExitExclusive()
    {
        // ... actual unlock
#if DEBUG
        _lockOrder.Value!.Pop();
#endif
    }
}
```

### Pros
- ✅ Catches actual violations at runtime
- ✅ Zero overhead in release builds
- ✅ Can validate complex invariants
- ✅ Great for testing

### Cons
- ❌ Only catches bugs when code path executes
- ❌ Not discoverable in API
- ❌ Debug-only (doesn't protect production)
- ❌ Some overhead even in debug builds

---

## Recommended Hybrid Approach for Typhon

Based on Typhon's requirements (microsecond-level performance, complex concurrency), I recommend a **layered approach**:

### Layer 1: Attributes (Foundation)
```csharp
// Define in Typhon.Engine/Annotations/
[ThreadSafe], [SingleThreaded], [ThreadLocal], [RequiresLock], [Immutable]
```

**Effort:** Low | **Value:** High | **Apply to:** All public APIs

### Layer 2: XML Documentation (Discoverability)
```csharp
/// <threadsafety>Thread-safe. Uses internal AccessControl locking.</threadsafety>
```

**Effort:** Medium | **Value:** Medium | **Apply to:** Complex APIs

### Layer 3: Roslyn Analyzers (Enforcement) ⭐
```csharp
// Start with 2-3 high-value analyzers:
// 1. TYPHON001: RequiresLock enforcement
// 2. TYPHON002: ThreadLocal validation
// 3. TYPHON004: Immutability validation
```

**Effort:** High (initial), Low (maintenance) | **Value:** Very High | **Apply to:** Entire solution

### Layer 4: Debug Assertions (Validation)
```csharp
#if DEBUG
    AssertSameThread();
    AssertLockHeld();
#endif
```

**Effort:** Low | **Value:** Medium | **Apply to:** Critical hot paths

### Layer 5: Type System (Where It Makes Sense)
```csharp
// Only for types where confusion is common:
SharedComponentTable vs ExclusiveComponentTable
```

**Effort:** High | **Value:** Situational | **Apply to:** Confusing APIs only

---

## Implementation Roadmap for Typhon

### Phase 1: Foundation (1-2 weeks)
1. ✅ Create `Typhon.Engine.Annotations` namespace
2. ✅ Define attributes: `[ThreadSafe]`, `[SingleThreaded]`, `[RequiresLock]`, `[Immutable]`, `[ThreadLocal]`
3. ✅ Annotate existing codebase (start with public APIs)
4. ✅ Update XML documentation template for `<threadsafety>`

### Phase 2: Analyzer Development (3-4 weeks)
1. ✅ Set up `Typhon.Analyzers` project structure
2. ✅ Implement TYPHON001: `RequiresLock` enforcement
3. ✅ Implement TYPHON002: `ThreadLocal` validation
4. ✅ Add code fix providers
5. ✅ Write comprehensive tests
6. ✅ Package as NuGet analyzer

### Phase 3: Validation (1-2 weeks)
1. ✅ Enable analyzers in Typhon.Engine
2. ✅ Fix violations found by analyzers
3. ✅ Add debug assertions to critical paths
4. ✅ Update documentation

### Phase 4: Refinement (Ongoing)
1. ✅ Add more analyzers based on real-world bugs
2. ✅ Refine rules based on feedback
3. ✅ Consider type-system enforcement for problematic APIs

---

## Example: Applying to Typhon.Engine

### Before (Unclear)
```csharp
public class ComponentTable
{
    private AccessControl _lock = new();

    private bool InternalRead(long entityId, out MyComponent comp)
    {
        // Who knows if lock is needed?
    }

    public bool Read(long entityId, out MyComponent comp)
    {
        // Is this thread-safe?
    }
}

public ref struct ChunkAccessor
{
    // Can I share this across threads?
}
```

### After (Crystal Clear)
```csharp
/// <summary>
/// Manages component storage with MVCC revision tracking.
/// </summary>
/// <threadsafety>
/// Thread-safe. All public methods handle internal synchronization.
/// Internal methods require caller to hold appropriate locks.
/// </threadsafety>
[ThreadSafe(AllMembers = true)]
public class ComponentTable
{
    private readonly AccessControl _lock = new();

    /// <summary>
    /// Internal read without locking. Caller must hold lock.
    /// </summary>
    [RequiresLock(nameof(_lock), LockMode.Shared)]
    private bool InternalRead(long entityId, out MyComponent comp)
    {
        // Analyzer ensures _lock is held
    }

    /// <summary>
    /// Thread-safe read operation.
    /// </summary>
    [ThreadSafe]
    public bool Read(long entityId, out MyComponent comp)
    {
        _lock.EnterShared();
        try { return InternalRead(entityId, out comp); }
        finally { _lock.ExitShared(); }
    }
}

/// <summary>
/// Provides cached access to chunk data for single-threaded use.
/// </summary>
/// <threadsafety>
/// NOT thread-safe. Each thread must use its own instance.
/// Designed for stack allocation or thread-local storage.
/// </threadsafety>
[SingleThreaded(Reason = "Maintains internal cache for performance. Use one per thread.")]
public ref struct ChunkAccessor
{
#if DEBUG
    private readonly int _threadId = Environment.CurrentManagedThreadId;
    private void AssertSameThread() => Debug.Assert(_threadId == Environment.CurrentManagedThreadId);
#else
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void AssertSameThread() { }
#endif

    public Span<byte> GetData(int index)
    {
        AssertSameThread(); // Catches misuse in tests
        // ...
    }
}
```

With analyzers enabled:
```csharp
private readonly AccessControl _lock = new();

[RequiresLock(nameof(_lock), LockMode.Shared)]
private bool InternalRead(long entityId, out MyComponent comp) { }

public void WrongUsage()
{
    InternalRead(123, out var comp);
    // ❌ Compiler Error TYPHON001: Method 'InternalRead' requires holding lock '_lock' in Shared mode
}

[SingleThreaded]
public ref struct ChunkAccessor { }

public class MyClass
{
    private ChunkAccessor _accessor;
    // ❌ Compiler Error TYPHON002: SingleThreaded type should not be stored in instance field. Use [ThreadStatic] or stack allocation.
}
```

---

## Conclusion

**For Typhon specifically**, the recommended approach is:

1. **Start with attributes** - Low effort, immediate documentation value
2. **Invest in Roslyn analyzers** - High ROI for catching concurrency bugs at compile time
3. **Add debug assertions** - Safety net during testing
4. **Use type system sparingly** - Only where confusion is proven to occur

The combination of `[RequiresLock]` with Roslyn analyzers would be particularly powerful for Typhon's lock-heavy architecture, catching bugs like calling internal methods without proper synchronization at compile time.
