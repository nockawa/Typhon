using System;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Typhon.Engine;

/// <summary>
/// 16-byte packed struct for deep mode operation log entries.
/// </summary>
[StructLayout(LayoutKind.Sequential, Pack = 1)]
public struct ResourceOperationEntry
{
    /// <summary>Timestamp from Stopwatch.GetTimestamp().</summary>
    public long Timestamp;           // 8 bytes

    /// <summary>Duration or wait time in microseconds.</summary>
    public uint DurationUs;          // 4 bytes

    /// <summary>Thread ID that performed the operation.</summary>
    public ushort ThreadId;          // 2 bytes

    /// <summary>Type of lock operation.</summary>
    public LockOperation Operation;  // 1 byte

    /// <summary>Reserved for future use.</summary>
    public byte Flags;               // 1 byte

    /// <summary>Returns true if this entry is empty (Operation == None).</summary>
    public bool IsEmpty => Operation == LockOperation.None;

    /// <summary>
    /// Creates a new operation entry with the current timestamp and thread ID.
    /// </summary>
    public static ResourceOperationEntry Create(LockOperation op, long durationUs)
        => new ResourceOperationEntry
        {
            Timestamp = Stopwatch.GetTimestamp(),
            DurationUs = (uint)Math.Min(durationUs, uint.MaxValue),
            ThreadId = (ushort)Environment.CurrentManagedThreadId,
            Operation = op,
            Flags = 0
        };
}

/// <summary>
/// Inline array of 6 ResourceOperationEntry structs (96 bytes total).
/// Fits in a 128-byte block with header.
/// </summary>
[InlineArray(Count)]
internal struct ResourceOperationBlock
{
    internal const int Count = 6;
    private ResourceOperationEntry _element;
}
