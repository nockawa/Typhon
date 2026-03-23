using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using JetBrains.Annotations;

namespace Typhon.Engine;

/// <summary>
/// Header of an entity record stored in the per-archetype LinearHash.
/// 14 bytes: BornTSN (6B) + DiedTSN (6B) + EnabledBits (2B).
/// Component locations (4B × N) follow immediately after in raw byte storage.
/// </summary>
/// <remarks>
/// TSN packing follows the same pattern as <c>CompRevStorageElement</c>: upper 32 bits + lower 16 bits = 48-bit TSN.
/// Non-aligned 6-byte fields are safe because the per-bucket OLC latch serializes writers and detects torn reads.
/// </remarks>
[StructLayout(LayoutKind.Sequential, Pack = 2)]
[PublicAPI]
public struct EntityRecordHeader
{
    // BornTSN: 48-bit transaction sequence number when entity was created+committed
    // 0 = genesis entity (always visible)
    public uint BornTsnHigh;
    public ushort BornTsnLow;

    // DiedTSN: 48-bit transaction sequence number when entity was destroyed+committed
    // 0 = alive
    public uint DiedTsnHigh;
    public ushort DiedTsnLow;

    // Per-component enable mask: bit N set = component slot N is enabled
    public ushort EnabledBits;

    /// <summary>48-bit BornTSN packed as upper 32 + lower 16.</summary>
    public long BornTSN
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        readonly get => ((long)BornTsnHigh << 16) | BornTsnLow;
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        set
        {
            BornTsnHigh = (uint)(value >> 16);
            BornTsnLow = (ushort)(value & 0xFFFF);
        }
    }

    /// <summary>48-bit DiedTSN packed as upper 32 + lower 16.</summary>
    public long DiedTSN
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        readonly get => ((long)DiedTsnHigh << 16) | DiedTsnLow;
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        set
        {
            DiedTsnHigh = (uint)(value >> 16);
            DiedTsnLow = (ushort)(value & 0xFFFF);
        }
    }

    /// <summary>True if the entity has not been destroyed (DiedTSN == 0).</summary>
    public readonly bool IsAlive
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => DiedTsnHigh == 0 && DiedTsnLow == 0;
    }

    /// <summary>
    /// Check whether this entity is visible to a transaction with the given TSN.
    /// </summary>
    /// <remarks>
    /// Visibility rules (snapshot isolation):
    /// <list type="bullet">
    /// <item>BornTSN == 0: genesis entity, always visible</item>
    /// <item>BornTSN != 0 and BornTSN > txTsn: not born yet → invisible</item>
    /// <item>DiedTSN == 0: alive → visible (if born)</item>
    /// <item>DiedTSN != 0 and DiedTSN &lt;= txTsn: dead at this snapshot → invisible</item>
    /// </list>
    /// </remarks>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public readonly bool IsVisibleAt(long txTsn)
    {
        var born = BornTSN;
        if (born != 0 && born > txTsn)
        {
            return false;
        }

        var died = DiedTSN;
        if (died != 0 && died <= txTsn)
        {
            return false;
        }

        return true;
    }
}

/// <summary>
/// Static accessor for entity records stored as raw bytes in the per-archetype RawValueHashMap.
/// Record layout: [EntityRecordHeader (14B)] [Location₀ (4B)] [Location₁ (4B)] ... [Location_{N-1} (4B)]
/// </summary>
[PublicAPI]
public static unsafe class EntityRecordAccessor
{
    /// <summary>Size of the <see cref="EntityRecordHeader"/> in bytes.</summary>
    public const int HeaderSize = 14;

    /// <summary>Maximum component count per archetype (16-bit EnabledBits).</summary>
    public const int MaxComponentCount = 16;

    /// <summary>Maximum entity record size in bytes: HeaderSize + MaxComponentCount × 4 = 78.</summary>
    public const int MaxRecordSize = HeaderSize + MaxComponentCount * sizeof(int);

    /// <summary>Total entity record size for an archetype with <paramref name="componentCount"/> components.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int RecordSize(int componentCount) => HeaderSize + componentCount * sizeof(int);

    /// <summary>Get a reference to the header at the start of the record.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static ref EntityRecordHeader GetHeader(byte* record) => ref Unsafe.AsRef<EntityRecordHeader>(record);

    /// <summary>Read the component chunk ID at the given slot index.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int GetLocation(byte* record, int slot) => *(int*)(record + HeaderSize + slot * sizeof(int));

    /// <summary>Write the component chunk ID at the given slot index.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void SetLocation(byte* record, int slot, int chunkId) => *(int*)(record + HeaderSize + slot * sizeof(int)) = chunkId;

    /// <summary>Bulk copy all component locations from one record to another.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void CopyLocations(byte* src, byte* dst, int componentCount) => 
        Unsafe.CopyBlock(dst + HeaderSize, src + HeaderSize, (uint)(componentCount * sizeof(int)));

    /// <summary>Zero-initialize an entire entity record (header + locations).</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void InitializeRecord(byte* record, int componentCount) => Unsafe.InitBlock(record, 0, (uint)RecordSize(componentCount));

    /// <summary>Copy component locations from a raw record into an <see cref="EntityLocations"/> struct.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static void CopyLocationsTo(byte* record, ref EntityLocations locs, int componentCount)
    {
        fixed (int* dst = locs.Values)
        {
            Unsafe.CopyBlock(dst, record + HeaderSize, (uint)(componentCount * sizeof(int)));
        }
    }
}

/// <summary>
/// Inline storage for up to 16 component location ChunkIds. Value type — stored contiguously in List backing arrays,
/// eliminating per-entity heap allocations during query enumeration.
/// </summary>
[StructLayout(LayoutKind.Sequential)]
internal unsafe struct EntityLocations
{
    public fixed int Values[EntityRecordAccessor.MaxComponentCount];
}
