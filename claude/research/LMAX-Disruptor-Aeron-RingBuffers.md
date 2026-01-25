# LMAX Disruptor & Aeron Log Buffer: High-Performance Ring Buffer Patterns

**Date:** 2026-01-23
**Status:** Concluded
**Outcome:** Detailed analysis of multi-producer ring buffer patterns for variable-size WAL records, with emphasis on the claim-then-publish two-phase approach and Aeron's log buffer design.

## Context

For Typhon's WAL (Write-Ahead Log) implementation, we need a high-throughput ring buffer that supports:
- Multiple concurrent producers (transactions committing simultaneously)
- Variable-size records (WAL entries vary in size)
- Ordered consumption (WAL entries must be replayed in order)
- Minimal contention and lock-free operation

Two systems are particularly relevant: the LMAX Disruptor (pioneered lock-free ring buffer patterns) and Aeron (solves the variable-size message problem).

## Questions to Answer

1. How does LMAX Disruptor handle multi-producer scenarios (MPSC)?
2. How are variable-size messages handled in ring buffers?
3. What is the Aeron log buffer approach to variable-size records?
4. How does back-pressure work when the ring is full?
5. How is ordering maintained with concurrent producers?
6. What .NET implementations exist?

## Analysis

---

### 1. LMAX Disruptor Multi-Producer Sequencing

#### Core Concept: The Sequence

The Disruptor uses a monotonically increasing 64-bit sequence number as the central coordination mechanism. Each slot in the ring buffer corresponds to `sequence % bufferSize`. The key insight is separating **claiming** a sequence from **publishing** it.

#### Single Producer (Simple Case)

With a single producer, no CAS is needed. The producer simply increments its cursor:

```
nextSequence = cursor + 1
// write data to slot[nextSequence % size]
cursor = nextSequence  // publish via volatile write
```

#### Multi-Producer Algorithm (MultiProducerSequencer)

The multi-producer case is more complex and uses a **two-phase protocol**:

**Phase 1: Claim (next/tryNext)**

```java
// Simplified MultiProducerSequencer.next(n)
long current;
long next;
do {
    current = cursor.get();           // read current cursor
    next = current + n;               // compute desired next

    // Check for wrap: ensure we don't lap the slowest consumer
    long wrapPoint = next - bufferSize;
    long cachedGatingSequence = gatingSequenceCache.get();

    if (wrapPoint > cachedGatingSequence || cachedGatingSequence > current) {
        long gatingSequence = Util.getMinimumSequence(gatingSequences, current);
        if (wrapPoint > gatingSequence) {
            // Ring is full - back-pressure!
            LockSupport.parkNanos(1);
            continue;
        }
        gatingSequenceCache.set(gatingSequence);
    }
} while (!cursor.compareAndSet(current, next));  // CAS to claim
// Producer now "owns" sequences [current+1 .. next]
return next;
```

Key points:
- The CAS on the cursor atomically reserves a contiguous range of slots
- Multiple producers compete via CAS - losers retry
- The `gatingSequences` array tracks all consumer positions to detect wrap

**Phase 2: Publish (publish)**

After writing data into the claimed slot, the producer marks it as available:

```java
// availableBuffer is an int[] of size bufferSize
// Each entry stores the "flag" (number of times that slot has been wrapped around)
private void setAvailable(long sequence) {
    int index = calculateIndex(sequence);       // sequence % bufferSize
    int flag = calculateAvailabilityFlag(sequence); // sequence / bufferSize
    availableBuffer[index] = flag;              // volatile write
}
```

The `availableBuffer` array is the key innovation for multi-producer:
- Each slot stores a "generation counter" (how many times the ring has wrapped)
- A consumer checks if slot N is published by verifying: `availableBuffer[N % size] == N / size`
- This allows out-of-order publication without blocking other producers

**Consumer Side: Detecting Contiguous Available Range**

```java
// Find the highest published sequence from a given start
long getHighestPublishedSequence(long lowerBound, long availableSequence) {
    for (long sequence = lowerBound; sequence <= availableSequence; sequence++) {
        if (!isAvailable(sequence)) {
            return sequence - 1;  // Gap found - stop here
        }
    }
    return availableSequence;
}
```

The consumer scans forward from its last consumed position, stopping at the first unpublished gap. This is the mechanism that converts potentially out-of-order publications into ordered consumption.

#### The "Slot Filled Out of Order" Problem

This is the central challenge: Producer A claims slot 5, Producer B claims slot 6. B finishes first and publishes slot 6. The consumer cannot process slot 6 until slot 5 is also published.

The Disruptor solves this with the `availableBuffer`:
- Consumer always scans from its last position forward
- It stops at the first gap (unpublished slot)
- Slot 6 being published before slot 5 is harmless - the consumer simply waits
- When A finally publishes slot 5, the consumer can advance past both 5 and 6

**Cost**: In the worst case, a slow producer can stall all consumers at that sequence. This is inherent to ordering guarantees.

---

### 2. Variable-Size Messages in Ring Buffers

The standard Disruptor uses **fixed-size slots** (one object per slot). This doesn't work for WAL records of varying sizes. Several approaches exist:

#### Approach A: Largest-Size Padding

Allocate slots for the maximum possible message size. Simple but wastes memory terribly for variable workloads.

#### Approach B: Indirection (Pointer-Based)

Store pointers/handles in fixed-size ring slots, actual data in a separate buffer. Adds indirection and memory management complexity.

#### Approach C: Byte-Stream Ring Buffer (Aeron Approach)

Treat the ring buffer as a contiguous byte stream. Producers claim variable-length regions. This is the approach Aeron uses and is most relevant for WAL.

#### Approach D: Multi-Slot Spanning

A single logical message can span multiple fixed-size slots. Requires framing headers and fragmentation/reassembly.

---

### 3. Aeron Log Buffer Design

Aeron's approach is the most sophisticated solution for variable-size records in a ring buffer context.

#### Architecture: Term Buffers

Aeron divides its log into three **term buffers** (typically 16MB-1GB each). At any time:
- One term is **active** (producers write here)
- One term is **clean** (ready to be written to next)
- One term is being **cleaned** (consumer finished, being zeroed)

This triple-buffer rotation avoids the classic ring wrap problem entirely.

#### Frame Structure

Every message in a term buffer is wrapped in a frame header:

```
+---------------------------------------------------------------+
|                       Frame Length (32-bit)                     |
+---------------------------------------------------------------+
|                       Frame Type (32-bit)                      |
|   (Includes version, flags, type identifier)                  |
+---------------------------------------------------------------+
|                       Term Offset (32-bit)                     |
+---------------------------------------------------------------+
|                       Session/Stream ID                        |
+---------------------------------------------------------------+
|                                                               |
|                       Message Body                            |
|                       (variable length, 8-byte aligned)       |
|                                                               |
+---------------------------------------------------------------+
```

Key: Frame length is written LAST (atomically) to signal completion.

#### The Claim Algorithm (TermAppender)

```
// Pseudocode for Aeron's claim mechanism
int claim(int messageLength) {
    int frameLength = messageLength + HEADER_LENGTH;
    int alignedLength = align(frameLength, FRAME_ALIGNMENT);  // 8-byte aligned

    int rawTail;
    int termOffset;
    do {
        rawTail = rawTailVolatile.get();   // atomic read of current tail
        termOffset = rawTail & 0x7FFFFFFF; // extract offset portion

        int resultingOffset = termOffset + alignedLength;
        if (resultingOffset > termLength) {
            // Term is full - need to rotate to next term
            return TERM_FULL;  // caller handles term rotation
        }
    } while (!rawTailVolatile.compareAndSet(rawTail,
              packRawTail(termId, termOffset + alignedLength)));  // CAS

    // Producer now owns bytes [termOffset .. termOffset + alignedLength)
    return termOffset;
}
```

Key insights:
- The tail position is advanced atomically via CAS
- Each producer gets a contiguous, non-overlapping region
- The CAS ensures no two producers claim overlapping regions
- Alignment to 8 bytes ensures atomic header writes

#### Publication (Commit) Protocol

After writing the message body, the producer commits by writing the frame length:

```
// Two-step publication:
// 1. Write message body at [offset + HEADER_SIZE]
buffer.putBytes(termOffset + HEADER_LENGTH, message, 0, messageLength);

// 2. Write frame header with length (ORDERED/Release write)
//    Frame length of 0 means "not yet published"
//    Negative frame length means "padding frame" (skip this)
buffer.putIntOrdered(termOffset, frameLength);  // This is the publish barrier
```

The frame length field serves as the publication flag:
- **0**: Slot not yet claimed or not yet published
- **Positive**: Valid frame, length indicates total frame size
- **Negative**: Padding frame (used to fill remaining space at end of term)

#### Consumer (Subscription) Read Protocol

```
// Consumer reads sequentially through the term buffer
int offset = lastConsumedOffset;
while (offset < termLength) {
    int frameLength = buffer.getIntVolatile(offset);

    if (frameLength == 0) {
        break;  // Hit unpublished region - stop and wait
    }

    if (frameLength < 0) {
        // Padding frame - skip to next term
        offset = termLength;
        break;
    }

    // Process frame at [offset + HEADER_LENGTH] with length (frameLength - HEADER_LENGTH)
    processMessage(buffer, offset + HEADER_LENGTH, frameLength - HEADER_LENGTH);

    offset += align(frameLength, FRAME_ALIGNMENT);
}
```

#### Handling the End-of-Term Gap

When a message doesn't fit in the remaining term space:

1. Producer detects `termOffset + alignedLength > termLength`
2. Producer writes a **padding frame** with negative length at current offset
3. The padding frame fills remaining bytes to end of term
4. Producer signals term rotation (move to next clean term)
5. Producer retries claim in the new active term

This avoids messages spanning term boundaries.

#### The Out-of-Order Problem in Aeron

Aeron has the same fundamental issue as Disruptor: Producer A claims offset 100, Producer B claims offset 200. If B writes first, the consumer reads offset 100, sees `frameLength == 0`, and waits.

Aeron mitigates this through:
1. **Fast writers**: The claim-write-publish sequence is very short (just a memcpy + header write)
2. **Busy-spin consumer**: Consumer can spin-wait on the frame length field
3. **No reordering within a term**: Messages appear in claim order, period

---

### 4. Back-Pressure Mechanisms

#### Disruptor Back-Pressure

The Disruptor provides several back-pressure strategies when the ring is full (producer would lap the slowest consumer):

1. **BlockingWaitStrategy**: Producer thread parks/blocks
2. **SleepingWaitStrategy**: Progressive sleep (spin -> yield -> sleep)
3. **BusySpinWaitStrategy**: Pure spin-wait (lowest latency, burns CPU)
4. **YieldingWaitStrategy**: Thread.yield() in a loop
5. **TimeoutBlockingWaitStrategy**: Block with timeout, then throw

The check occurs in the `next()` method:
```
wrapPoint = next - bufferSize
if (wrapPoint > minimumConsumerSequence) {
    // FULL - apply back-pressure strategy
}
```

#### Aeron Back-Pressure

Aeron uses a different model:

1. **Term Rotation**: When a term is full, rotate to the next term
2. **Publication Failure**: If all terms are full (consumer too slow), `offer()` returns `BACK_PRESSURED`
3. **Caller Decides**: The caller can retry, drop, or block - Aeron doesn't decide for you
4. **Flow Control**: Aeron supports configurable flow control strategies per subscription

```java
// Aeron publication attempt
long result = publication.offer(buffer, offset, length);
if (result == Publication.BACK_PRESSURED) {
    // Ring is full - caller must decide what to do
    idleStrategy.idle();  // e.g., spin then yield then park
}
```

#### For WAL Design Implications

For a WAL ring buffer:
- **Cannot drop messages** (every commit must be logged)
- **Must block producers** when ring is full (back-pressure to transactions)
- **Bounded latency**: Use adaptive spin (like Typhon's AdaptiveWaiter) then park
- **Monitor consumer lag**: Alert if consumer falls too far behind

---

### 5. Ordering Guarantees with Concurrent Producers

#### Claim-Order vs Completion-Order

The fundamental guarantee: **messages are ordered by their claim sequence, not by when they complete writing**. This means:

- If Transaction A claims slot/offset before Transaction B, A's record appears first in the log
- Even if B finishes writing first, the consumer respects claim order
- This matches transaction commit ordering requirements

#### How Ordering Is Enforced

**Disruptor**: The `availableBuffer` + sequential scan ensures ordered consumption.

**Aeron**: The sequential frame layout + `frameLength == 0` sentinel ensures ordered consumption. The consumer cannot skip past an uncommitted frame.

#### Implications for WAL

For a WAL, this ordering has specific implications:

1. **Claim order = commit order**: The CAS on the tail defines the serialization point
2. **Durability window**: Between claim and publish, the record is invisible but space is reserved
3. **Crash semantics**: If a producer crashes between claim and publish:
   - Disruptor: The slot remains unpublished forever (consumer stalls)
   - Aeron: `frameLength == 0` means the consumer stalls at that offset

4. **Timeout/recovery**: Must have a mechanism to detect and handle abandoned claims:
   - Heartbeat from producers
   - Timeout on claim-to-publish duration
   - Write a "tombstone" or "abort" frame to unblock the consumer

---

### 6. .NET Implementations

#### Disruptor-net

The official .NET port of the LMAX Disruptor:
- Repository: `disruptor-net/Disruptor-net` on GitHub
- Supports .NET 6+
- Full port of MultiProducerSequencer, all wait strategies
- Fixed-size slots only (object-per-slot model)
- Good for event-driven architectures but not directly for variable-size WAL

#### System.Threading.Channels

.NET's built-in channel primitives:
- `Channel.CreateBounded<T>(capacity)` provides bounded MPSC/MPMC
- Uses a ring buffer internally for bounded channels
- Has back-pressure built in (`WaitToWriteAsync`)
- Fixed-type slots (generics), not byte-stream
- Higher overhead than custom ring buffer (allocations, virtual dispatch)

#### Custom Byte-Stream Ring Buffers in .NET

For variable-size WAL records, the key .NET primitives:

```csharp
// Key building blocks for a .NET Aeron-style ring buffer:

// 1. Atomic tail claim (equivalent to CAS on offset)
int claimed = Interlocked.CompareExchange(ref _tail,
    currentTail + alignedLength, currentTail);

// 2. Memory-mapped backing store
MemoryMappedFile mmf = MemoryMappedFile.CreateFromFile(path, ...);
MemoryMappedViewAccessor accessor = mmf.CreateViewAccessor();

// 3. Direct pointer access for zero-copy writes
byte* ptr = null;
accessor.SafeMemoryMappedViewHandle.AcquirePointer(ref ptr);

// 4. Volatile write for publication (frame length)
Volatile.Write(ref *(int*)(ptr + offset), frameLength);

// 5. Memory barriers
Thread.MemoryBarrier();  // full fence
Interlocked.MemoryBarrier();  // same thing
```

#### Relevant .NET Libraries

1. **RingBuffer (NuGet)**: Simple fixed-size ring buffer, not MPSC
2. **FASTER Log**: Microsoft's concurrent log library, somewhat similar concepts
3. **System.IO.Pipelines**: .NET's Pipe pattern (PipeWriter/PipeReader) provides:
   - Variable-size writes with `GetMemory(sizeHint)`
   - Back-pressure via `FlushAsync()` pausing when reader is slow
   - However: single-writer design, uses managed memory, GC-aware
4. **Bedrock.Framework / Kestrel internals**: Use ring buffer patterns internally

---

## Synthesis: Design for Typhon WAL Ring Buffer

### Recommended Approach: Aeron-Inspired Term Buffer

Based on this analysis, the recommended approach for Typhon combines elements:

#### Data Structure

```
WAL Ring Buffer (e.g., 64MB total):
├── Term Buffer 0 (e.g., 16MB) - Active
├── Term Buffer 1 (e.g., 16MB) - Clean
├── Term Buffer 2 (e.g., 16MB) - Being consumed
└── Term Buffer 3 (e.g., 16MB) - Being cleaned
```

#### Frame Layout (per WAL entry)

```
+-------------------------------------------+
| Frame Length (4 bytes, atomic publish)     |  // 0 = unclaimed, negative = padding
+-------------------------------------------+
| Transaction ID (8 bytes)                  |
+-------------------------------------------+
| Commit Timestamp (8 bytes)                |
+-------------------------------------------+
| Entry Type + Flags (4 bytes)              |
+-------------------------------------------+
| Checksum (4 bytes)                        |
+-------------------------------------------+
| Payload (variable, 8-byte aligned)        |
|   - Component type, entity ID, data...   |
+-------------------------------------------+
```

#### Producer Protocol

```csharp
// 1. CLAIM: Atomically reserve space
int offset;
do {
    int currentTail = Volatile.Read(ref _activeTerm.Tail);
    int required = AlignUp(HEADER_SIZE + payloadSize, 8);
    int newTail = currentTail + required;

    if (newTail > TERM_SIZE) {
        // Write padding frame, rotate term
        RotateTerm();
        continue;
    }

    offset = Interlocked.CompareExchange(ref _activeTerm.Tail, newTail, currentTail);
} while (offset != currentTail);

// 2. WRITE: Copy data into claimed region
WriteHeader(offset, transactionId, timestamp, entryType);
WritePayload(offset + HEADER_SIZE, payload);

// 3. PUBLISH: Atomic frame length write (release semantics)
Volatile.Write(ref *(int*)(_termBase + offset), frameLength);
```

#### Consumer Protocol

```csharp
// Sequential scan through term
int offset = _lastConsumed;
while (offset < TERM_SIZE) {
    int frameLength = Volatile.Read(ref *(int*)(_termBase + offset));

    if (frameLength == 0) {
        // Hit unpublished claim - wait
        _waiter.SpinWait();
        continue;
    }
    if (frameLength < 0) {
        // Padding - advance to next term
        break;
    }

    ProcessWalEntry(offset + HEADER_SIZE, frameLength - HEADER_SIZE);
    offset += AlignUp(frameLength, 8);
}
```

#### Crash Recovery

Since `frameLength == 0` means either unclaimed or claimed-but-unpublished:
- On recovery, scan forward from last known good position
- If `frameLength == 0` found, this is the end of the valid log
- All data after this point is discarded (incomplete claims)
- Transactions whose WAL entries are incomplete are rolled back

#### Back-Pressure Strategy

```csharp
// Use Typhon's AdaptiveWaiter pattern
if (allTermsFull) {
    _adaptiveWaiter.Wait();  // spin -> yield -> park progression
    // Optionally: signal consumer to speed up
}
```

---

## Key Insights

| Aspect | LMAX Disruptor | Aeron Log Buffer | Recommendation for Typhon |
|--------|---------------|------------------|---------------------------|
| Message size | Fixed slots | Variable (frame headers) | Variable (WAL entries vary) |
| Claim mechanism | CAS on sequence counter | CAS on tail offset | CAS on tail offset |
| Publication | availableBuffer flag array | Frame length atomic write | Frame length atomic write |
| Consumer ordering | Sequential scan of availableBuffer | Sequential scan of frame lengths | Sequential scan |
| Ring wrap | Sequence modulo | Term rotation (triple buffer) | Term rotation |
| Back-pressure | Wait strategies (spin/yield/park) | Return BACK_PRESSURED to caller | AdaptiveWaiter spin-then-park |
| Crash safety | Not inherently crash-safe | Frame length = 0 means incomplete | Same + checksum validation |

## Critical Design Decisions

1. **Alignment**: All frames must be 8-byte aligned for atomic 64-bit header writes on x86/ARM
2. **Term size**: Larger terms = fewer rotations but more memory; 16-64MB is typical
3. **Number of terms**: Minimum 3 (active, clean, consuming); 4 gives more headroom
4. **Abandoned claim detection**: Need timeout mechanism for producers that crash mid-write
5. **Checksum placement**: Inside frame (before payload) enables per-entry validation on recovery

## References

- LMAX Disruptor Technical Paper: "LMAX Disruptor: High Performance Inter-Thread Messaging Library"
- Aeron Design Documentation: https://github.com/real-logic/aeron/wiki
- Martin Thompson (Mechanical Sympathy blog): Detailed posts on Disruptor internals
- Disruptor-net: https://github.com/disruptor-net/Disruptor-net
- "Trading at the Speed of Light" - Donald MacKenzie (context on LMAX's requirements)

## Next Steps

- [ ] Prototype the CAS-based claim mechanism with variable-size frames
- [ ] Benchmark term rotation vs modulo-wrap approaches
- [ ] Design the crash recovery scan algorithm
- [ ] Evaluate memory-mapped vs heap-backed term buffers for Typhon's use case
- [ ] Consider whether Typhon's AdaptiveWaiter can serve as the back-pressure primitive
