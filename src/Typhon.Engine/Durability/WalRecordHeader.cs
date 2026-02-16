using JetBrains.Annotations;
using System;
using System.Runtime.InteropServices;

namespace Typhon.Engine;

/// <summary>
/// Bit flags for WAL record metadata.
/// </summary>
[Flags]
[PublicAPI]
public enum WalRecordFlags : byte
{
    /// <summary>No flags set.</summary>
    None = 0,

    /// <summary>This record is the first in a Unit of Work.</summary>
    UowBegin = 1 << 0,

    /// <summary>This record is the last in a Unit of Work (commit marker).</summary>
    UowCommit = 1 << 1,

    /// <summary>The payload is compressed.</summary>
    Compressed = 1 << 2,

    /// <summary>The payload contains a full page image for torn-page repair.</summary>
    FullPageImage = 1 << 3,
}

/// <summary>
/// Operation type for a WAL record.
/// </summary>
[PublicAPI]
public enum WalOperationType : byte
{
    /// <summary>Component creation.</summary>
    Create = 1,

    /// <summary>Component update (before/after image).</summary>
    Update = 2,

    /// <summary>Component deletion (before image only).</summary>
    Delete = 3,
}

/// <summary>
/// 48-byte WAL record header written before each component change in the WAL. Sequential layout with Pack=1 for exact binary format.
/// </summary>
/// <remarks>
/// <para>
/// This header is part of the on-disk WAL format (WAL-Design.md section 2.2). Each WAL record is: <c>[WalRecordHeader (48 bytes)] [payload (PayloadLength bytes)]</c>.
/// </para>
/// <para>
/// CRC computation is not performed by this struct — that responsibility belongs to the WAL serialization layer (issue #55).
/// </para>
/// </remarks>
[StructLayout(LayoutKind.Sequential, Pack = 1)]
[PublicAPI]
public struct WalRecordHeader
{
    /// <summary>Log Sequence Number — monotonically increasing, globally unique.</summary>
    public long LSN;

    /// <summary>MVCC transaction timestamp for snapshot isolation.</summary>
    public long TransactionTSN;

    /// <summary>Total record length in bytes (header + payload). Used for skip-ahead.</summary>
    public uint TotalRecordLength;

    /// <summary>Unit of Work registry link — identifies the UoW this record belongs to.</summary>
    public ushort UowEpoch;

    /// <summary>Component table ID — identifies which component type was modified.</summary>
    public ushort ComponentTypeId;

    /// <summary>Primary key (entity ID) of the modified entity.</summary>
    public long EntityId;

    /// <summary>Number of component data bytes following this header.</summary>
    public ushort PayloadLength;

    /// <summary>Type of operation (Create, Update, Delete).</summary>
    public byte OperationType;

    /// <summary>Flags providing additional record metadata.</summary>
    public byte Flags;

    /// <summary>CRC32C of the previous WAL record for chain validation.</summary>
    public uint PrevCRC;

    /// <summary>CRC32C of this header + payload (computed during serialization).</summary>
    public uint CRC;

    /// <summary>Reserved for future use and alignment padding.</summary>
    public uint Reserved;

    /// <summary>Expected size of this struct in bytes.</summary>
    public const int SizeInBytes = 48;
}
