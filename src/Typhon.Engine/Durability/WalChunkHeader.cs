using JetBrains.Annotations;
using System.Runtime.InteropServices;

namespace Typhon.Engine;

/// <summary>
/// Chunk type discriminator for the generic WAL chunk envelope.
/// </summary>
[PublicAPI]
public enum WalChunkType : ushort
{
    /// <summary>Transaction record: component Create/Update/Delete.</summary>
    Transaction = 1,

    /// <summary>Full-Page Image for torn-page repair.</summary>
    FullPageImage = 2,
}

/// <summary>
/// 8-byte generic chunk header written before every WAL chunk (transaction record or FPI).
/// </summary>
/// <remarks>
/// <para>
/// Producers write <see cref="PrevCRC"/> = 0 and the footer CRC = 0 as placeholders. The single-threaded WAL writer (<see cref="WalWriter"/>) patches both
/// fields after staging buffer copy and before disk write — this centralizes CRC chain management and eliminates the PrevCRC chain break that occurred when
/// FPI records interleaved with transaction records.
/// </para>
/// <para>
/// <see cref="ChunkSize"/> enables forward-compatible skipping of unknown chunk types.
/// CRC covers bytes [0, ChunkSize - 4) — the entire header (including PrevCRC) + body.
/// </para>
/// </remarks>
[StructLayout(LayoutKind.Sequential, Pack = 1)]
[PublicAPI]
public struct WalChunkHeader
{
    /// <summary>Chunk type discriminator.</summary>
    public ushort ChunkType;

    /// <summary>Total chunk size in bytes: header (8) + body + footer (4).</summary>
    public ushort ChunkSize;

    /// <summary>Footer CRC of the previous chunk. Set by WAL writer; producers write 0.</summary>
    public uint PrevCRC;

    /// <summary>Expected size of this struct in bytes.</summary>
    public const int SizeInBytes = 8;
}

/// <summary>
/// 4-byte chunk footer written after every WAL chunk body.
/// </summary>
/// <remarks>
/// CRC32C is computed over [0, ChunkSize - 4) — the chunk header + body, excluding this footer.
/// Set by the WAL writer thread; producers write 0 as a placeholder.
/// </remarks>
[StructLayout(LayoutKind.Sequential, Pack = 1)]
[PublicAPI]
public struct WalChunkFooter
{
    /// <summary>CRC32C over the chunk header + body (excludes this footer).</summary>
    public uint CRC;

    /// <summary>Expected size of this struct in bytes.</summary>
    public const int SizeInBytes = 4;
}
