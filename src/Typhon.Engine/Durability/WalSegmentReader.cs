using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Typhon.Engine;

/// <summary>
/// Sequential reader for WAL segment files during crash recovery. Opens a segment, validates the header,
/// and iterates records with CRC chain validation. Stops at end-of-data or CRC break (truncation).
/// </summary>
internal sealed class WalSegmentReader : IDisposable
{
    private const int PageSize = 4096;

    private readonly IWalFileIO _fileIO;

    // Staging buffer for aligned reads
    private byte[] _readBuffer;
    private readonly int _readBufferSize;

    // Segment data loaded into memory
    private byte[] _segmentData;
    private int _segmentDataLength;

    // Frame-level iteration
    private int _frameOffset;         // Start of current frame (WalFrameHeader position)
    private int _frameEnd;            // End of current frame
    private int _recordsRemainingInFrame;

    // Record-level iteration within a frame
    private int _recordOffset;        // Current record position within frame

    // CRC chain state
    private uint _lastRecordCrc;
    private bool _isFirstRecord;
    private bool _opened;

    private WalSegmentHeader _segmentHeader;

    /// <summary>The validated segment header.</summary>
    public ref readonly WalSegmentHeader SegmentHeader => ref _segmentHeader;

    /// <summary>True if a CRC chain break was detected during iteration (indicates crash truncation).</summary>
    public bool WasTruncated { get; private set; }

    /// <summary>The LSN of the last successfully validated record.</summary>
    public long LastValidLSN { get; private set; }

    /// <summary>Number of records successfully read.</summary>
    public int RecordsRead { get; private set; }

    public WalSegmentReader(IWalFileIO fileIO)
    {
        ArgumentNullException.ThrowIfNull(fileIO);
        _fileIO = fileIO;
        _readBufferSize = 256 * 1024; // 256KB read buffer
        _readBuffer = GC.AllocateArray<byte>(_readBufferSize, pinned: true);
    }

    /// <summary>
    /// Opens and validates a WAL segment file. Returns true if the header is valid.
    /// </summary>
    public bool OpenSegment(string path)
    {
        if (!_fileIO.Exists(path))
        {
            return false;
        }

        using var handle = _fileIO.OpenSegment(path, withFUA: false);

        // Read the header (first 4096 bytes)
        var headerBuffer = new byte[WalSegmentHeader.SizeInBytes];
        _fileIO.ReadAligned(handle, 0, headerBuffer);

        _segmentHeader = MemoryMarshal.Read<WalSegmentHeader>(headerBuffer);

        if (!_segmentHeader.Validate())
        {
            return false;
        }

        // Read the full segment data area (after header) into memory
        var dataSize = (int)_segmentHeader.SegmentSize - WalSegmentHeader.SizeInBytes;
        if (dataSize <= 0)
        {
            _segmentData = [];
            _segmentDataLength = 0;
        }
        else
        {
            _segmentData = new byte[dataSize];
            var remaining = dataSize;
            var fileOffset = (long)WalSegmentHeader.SizeInBytes;
            var destOffset = 0;

            while (remaining > 0)
            {
                var toRead = Math.Min(remaining, _readBufferSize);
                var alignedRead = AlignUp(toRead, PageSize);
                if (alignedRead > _readBufferSize)
                {
                    alignedRead = _readBufferSize;
                    toRead = alignedRead;
                }

                _fileIO.ReadAligned(handle, fileOffset, _readBuffer.AsSpan(0, alignedRead));

                var toCopy = Math.Min(toRead, remaining);
                _readBuffer.AsSpan(0, toCopy).CopyTo(_segmentData.AsSpan(destOffset));

                fileOffset += alignedRead;
                destOffset += toCopy;
                remaining -= toCopy;
            }

            _segmentDataLength = dataSize;
        }

        _opened = true;
        _frameOffset = 0;
        _frameEnd = 0;
        _recordOffset = 0;
        _recordsRemainingInFrame = 0;
        _isFirstRecord = true;
        _lastRecordCrc = 0;
        WasTruncated = false;
        LastValidLSN = 0;
        RecordsRead = 0;

        return true;
    }

    /// <summary>
    /// Reads the next WAL record from the segment. Returns false at end-of-data or CRC break.
    /// </summary>
    /// <param name="header">The record header.</param>
    /// <param name="payload">The record payload data.</param>
    /// <returns>True if a valid record was read; false at end-of-segment or on CRC break.</returns>
    public bool TryReadNext(out WalRecordHeader header, out ReadOnlySpan<byte> payload)
    {
        header = default;
        payload = default;

        if (!_opened || _segmentData == null)
        {
            return false;
        }

        // Advance to next frame if we've consumed all records in the current one
        while (_recordsRemainingInFrame == 0)
        {
            if (!AdvanceToNextFrame())
            {
                return false;
            }
        }

        // Read the record at _recordOffset
        if (_recordOffset + WalRecordHeader.SizeInBytes > _frameEnd)
        {
            // Not enough room for a record header — frame is corrupt or exhausted
            _recordsRemainingInFrame = 0;
            return false;
        }

        header = Unsafe.As<byte, WalRecordHeader>(ref _segmentData[_recordOffset]);

        // Validate total record length
        if (header.TotalRecordLength < WalRecordHeader.SizeInBytes)
        {
            WasTruncated = true;
            return false;
        }

        if (_recordOffset + (int)header.TotalRecordLength > _frameEnd)
        {
            WasTruncated = true;
            return false;
        }

        // Extract payload
        var payloadStart = _recordOffset + WalRecordHeader.SizeInBytes;
        var payloadLen = header.PayloadLength;
        if (payloadStart + payloadLen > _frameEnd)
        {
            WasTruncated = true;
            return false;
        }

        payload = _segmentData.AsSpan(payloadStart, payloadLen);

        // CRC validation: compute CRC over header+payload with CRC field zeroed
        var recordSpan = _segmentData.AsSpan(_recordOffset, WalRecordHeader.SizeInBytes + payloadLen);
        var crcFieldOffset = (int)Marshal.OffsetOf<WalRecordHeader>(nameof(WalRecordHeader.CRC));
        var computedCrc = WalCrc.ComputeSkipping(recordSpan, crcFieldOffset, sizeof(uint));

        if (computedCrc != header.CRC)
        {
            WasTruncated = true;
            return false;
        }

        // PrevCRC chain validation
        if (_isFirstRecord)
        {
            _isFirstRecord = false;
        }
        else if (header.PrevCRC != 0 && header.PrevCRC != _lastRecordCrc)
        {
            // CRC chain break (PrevCRC=0 on first record of a new UoW is acceptable)
            WasTruncated = true;
            return false;
        }

        _lastRecordCrc = header.CRC;
        LastValidLSN = header.LSN;
        RecordsRead++;
        _recordsRemainingInFrame--;

        // Advance record offset to next record
        _recordOffset += (int)header.TotalRecordLength;

        return true;
    }

    /// <summary>
    /// Advances to the next non-empty frame. Returns false at end-of-data.
    /// </summary>
    private bool AdvanceToNextFrame()
    {
        // If we were mid-frame, jump to end of current frame
        if (_frameEnd > 0)
        {
            _frameOffset = _frameEnd;
        }

        while (_frameOffset < _segmentDataLength)
        {
            if (_frameOffset + WalFrameHeader.SizeInBytes > _segmentDataLength)
            {
                return false;
            }

            ref var frameHeader = ref Unsafe.As<byte, WalFrameHeader>(ref _segmentData[_frameOffset]);

            // Zero frame length = end-of-data (not yet published)
            if (frameHeader.FrameLength == 0)
            {
                return false;
            }

            // Padding sentinel = end of usable data
            if (frameHeader.FrameLength == WalFrameHeader.PaddingSentinel)
            {
                return false;
            }

            // Validate frame bounds
            if (frameHeader.FrameLength < WalFrameHeader.SizeInBytes ||
                _frameOffset + frameHeader.FrameLength > _segmentDataLength)
            {
                WasTruncated = true;
                return false;
            }

            _frameEnd = _frameOffset + frameHeader.FrameLength;
            _recordOffset = _frameOffset + WalFrameHeader.SizeInBytes;
            _recordsRemainingInFrame = frameHeader.RecordCount;

            if (_recordsRemainingInFrame > 0)
            {
                return true;
            }

            // Empty frame (abandoned claim) — skip to next
            _frameOffset = _frameEnd;
        }

        return false;
    }

    public void Dispose()
    {
        _segmentData = null;
        _readBuffer = null;
        _opened = false;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int AlignUp(int value, int alignment) => (value + alignment - 1) & ~(alignment - 1);
}
