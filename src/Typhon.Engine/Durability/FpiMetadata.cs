using System.Runtime.InteropServices;

namespace Typhon.Engine;

/// <summary>
/// 16-byte blittable metadata written immediately after the <see cref="WalRecordHeader"/> in an FPI WAL record.
/// Identifies which data-file page the full-page image belongs to.
/// </summary>
/// <remarks>
/// <para>
/// Uncompressed layout: [WalRecordHeader (48 B)] [FpiMetadata (16 B)] [Page data (8192 B)] = 8256 B total.
/// Compressed layout:   [WalRecordHeader (48 B)] [FpiMetadata (16 B)] [Compressed data (variable)] — smaller total.
/// </para>
/// <para>
/// When <see cref="CompressionAlgo"/> is 0 (none), the page data immediately follows at full <see cref="PagedMMF.PageSize"/>.
/// When <see cref="CompressionAlgo"/> is 1 (LZ4), the data is LZ4-compressed and <see cref="UncompressedSize"/> records the original size.
/// The <see cref="WalRecordFlags.Compressed"/> flag on the WAL header indicates compressed payloads.
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

    /// <summary>Uncompressed page size in bytes. Always <see cref="PagedMMF.PageSize"/> (8192). Used during decompression to allocate the target buffer.</summary>
    public ushort UncompressedSize;

    /// <summary>Compression algorithm applied to the page payload: 0 = none (<see cref="FpiCompression.AlgoNone"/>), 1 = LZ4 (<see cref="FpiCompression.AlgoLZ4"/>).</summary>
    public byte CompressionAlgo;

    /// <summary>Padding for 16-byte alignment.</summary>
    public byte Reserved;

    /// <summary>Expected size of this struct in bytes.</summary>
    public const int SizeInBytes = 16;
}
