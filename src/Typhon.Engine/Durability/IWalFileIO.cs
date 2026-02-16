using JetBrains.Annotations;
using Microsoft.Win32.SafeHandles;
using System;

namespace Typhon.Engine;

/// <summary>
/// Platform abstraction for WAL segment file I/O operations.
/// Encapsulates O_DIRECT + FUA + pre-allocation semantics.
/// </summary>
/// <remarks>
/// <para>
/// Implementations must ensure that <see cref="WriteAligned"/> writes are aligned to 4096-byte boundaries
/// (both offset and size). This is required by <c>FILE_FLAG_NO_BUFFERING</c> (Windows) / <c>O_DIRECT</c> (Linux).
/// </para>
/// <para>
/// Two implementations exist:
/// <list type="bullet">
///   <item><see cref="WalFileIO"/> — production implementation using <c>File.OpenHandle</c></item>
///   <item><see cref="InMemoryWalFileIO"/> — test mock for unit tests (no disk I/O)</item>
/// </list>
/// </para>
/// </remarks>
[PublicAPI]
public interface IWalFileIO : IDisposable
{
    /// <summary>
    /// Opens a WAL segment file with O_DIRECT semantics and optional FUA (Force Unit Access).
    /// </summary>
    /// <param name="path">File path for the segment.</param>
    /// <param name="withFUA">
    /// If true, enables per-write durability: <c>FILE_FLAG_WRITE_THROUGH</c> (Windows) / <c>O_DSYNC</c> (Linux).
    /// Used for Immediate durability mode.
    /// </param>
    /// <returns>A safe handle to the opened segment file.</returns>
    SafeFileHandle OpenSegment(string path, bool withFUA);

    /// <summary>
    /// Writes a 4096-byte-aligned buffer to the file at the given offset.
    /// Both <paramref name="offset"/> and <paramref name="data"/> length must be multiples of 4096.
    /// </summary>
    /// <param name="handle">File handle from <see cref="OpenSegment"/>.</param>
    /// <param name="offset">File offset to write at (must be 4096-aligned).</param>
    /// <param name="data">Data to write (length must be a multiple of 4096).</param>
    void WriteAligned(SafeFileHandle handle, long offset, ReadOnlySpan<byte> data);

    /// <summary>
    /// Flushes file buffers to stable media. Used for GroupCommit mode where per-write FUA is not enabled.
    /// Windows: <c>FlushFileBuffers</c>. Linux: <c>fdatasync</c>.
    /// </summary>
    /// <param name="handle">File handle to flush.</param>
    void FlushBuffers(SafeFileHandle handle);

    /// <summary>
    /// Pre-allocates a file to the given size to avoid filesystem metadata writes during normal operation.
    /// </summary>
    /// <param name="handle">File handle to extend.</param>
    /// <param name="size">Desired file size in bytes.</param>
    void PreAllocate(SafeFileHandle handle, long size);

    /// <summary>
    /// Checks whether a segment file exists at the given path.
    /// </summary>
    /// <param name="path">File path to check.</param>
    /// <returns><c>true</c> if the segment exists; otherwise <c>false</c>.</returns>
    bool Exists(string path);
}
