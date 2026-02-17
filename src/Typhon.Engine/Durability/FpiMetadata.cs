using System.Runtime.InteropServices;

namespace Typhon.Engine;

/// <summary>
/// 16-byte blittable metadata written immediately after the <see cref="WalRecordHeader"/> in an FPI WAL record.
/// Identifies which data-file page the full-page image belongs to.
/// </summary>
/// <remarks>
/// <para>
/// Layout: [WalRecordHeader (48 B)] [FpiMetadata (16 B)] [Page data (8192 B)] = 8256 B total.
/// </para>
/// <para>
/// <see cref="CompressionAlgo"/> and <see cref="UncompressedSize"/> are reserved for Phase 5 (LZ4 compression).
/// Currently <see cref="CompressionAlgo"/> is always 0 (none) and <see cref="UncompressedSize"/> equals <see cref="PagedMMF.PageSize"/>.
/// </para>
/// </remarks>
[StructLayout(LayoutKind.Sequential, Pack = 1)]
internal struct FpiMetadata
{
    /// <summary>Global page index in the data file.</summary>
    public int FilePageIndex;

    /// <summary>Reserved for future multi-segment support (always 0 for now).</summary>
    public int SegmentId;

    /// <summary>ChangeRevision of the page at capture time.</summary>
    public int ChangeRevision;

    /// <summary>Uncompressed page size in bytes (always 8192, forward-compat for Phase 5 compression).</summary>
    public ushort UncompressedSize;

    /// <summary>Compression algorithm: 0 = none, 1 = LZ4 (Phase 5).</summary>
    public byte CompressionAlgo;

    /// <summary>Padding for 16-byte alignment.</summary>
    public byte Reserved;

    /// <summary>Expected size of this struct in bytes.</summary>
    public const int SizeInBytes = 16;
}
