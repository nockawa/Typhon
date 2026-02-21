using JetBrains.Annotations;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;

namespace Typhon.Engine;

/// <summary>
/// Result of a WAL crash recovery operation.
/// </summary>
[PublicAPI]
public struct WalRecoveryResult
{
    /// <summary>Number of WAL segment files scanned.</summary>
    public int SegmentsScanned;

    /// <summary>Total number of valid records scanned across all segments.</summary>
    public int RecordsScanned;

    /// <summary>Number of UoWs promoted from Pending to WalDurable (had commit markers in WAL).</summary>
    public int UowsPromoted;

    /// <summary>Number of UoWs voided (Pending with no commit marker in WAL).</summary>
    public int UowsVoided;

    /// <summary>Number of records replayed during recovery.</summary>
    public int RecordsReplayed;

    /// <summary>Number of FPI records applied for torn-page repair.</summary>
    public int FpiRecordsApplied;

    /// <summary>LSN of the last valid record found during scan.</summary>
    public long LastValidLSN;

    /// <summary>Total elapsed time for the recovery operation in microseconds.</summary>
    public long ElapsedMicroseconds;
}

/// <summary>
/// Tracks per-UoW state during WAL scan.
/// </summary>
internal class UowScanState
{
    public bool HasBegin;
    public bool HasCommit;
    public List<(WalRecordHeader Header, byte[] Payload)> Records = [];
}

/// <summary>
/// Tracks the most recent FPI for a given file page index during WAL scan.
/// </summary>
internal class FpiScanEntry
{
    public long LSN;
    public byte[] PageData;
    public FpiMetadata Metadata;
}

/// <summary>
/// Orchestrates WAL crash recovery: scans WAL segments, identifies committed UoWs,
/// voids pending ones, and replays committed records to restore data consistency.
/// </summary>
internal sealed class WalRecovery : IDisposable
{
    private readonly IWalFileIO _fileIO;
    private readonly string _walDirectory;
    private readonly PagedMMF _mmf;

    public WalRecovery(IWalFileIO fileIO, string walDirectory, PagedMMF mmf = null)
    {
        ArgumentNullException.ThrowIfNull(fileIO);
        ArgumentNullException.ThrowIfNull(walDirectory);
        _fileIO = fileIO;
        _walDirectory = walDirectory;
        _mmf = mmf;
    }

    /// <summary>
    /// Runs the full 6-phase recovery algorithm:
    /// Phase 1 — Discover segments, Phase 2 — Scan records + collect FPIs, Phase 3 — Cross-reference with registry, Phase 4 — FPI torn-page repair,
    /// Phase 5 — Replay committed records, Phase 6 — Finalize.
    /// </summary>
    /// <param name="registry">The UoW registry (loaded via <see cref="UowRegistry.LoadFromDiskRaw"/>).</param>
    /// <param name="checkpointLSN">LSN up to which data is already checkpointed. 0 = scan all.</param>
    /// <param name="dbe">The database engine for record replay.</param>
    /// <returns>Recovery statistics.</returns>
    public WalRecoveryResult Recover(UowRegistry registry, long checkpointLSN, DatabaseEngine dbe)
    {
        var startTicks = Stopwatch.GetTimestamp();
        var result = new WalRecoveryResult();

        // ═══════════════════════════════════════════════════════════
        // Phase 1: Discover segments
        // ═══════════════════════════════════════════════════════════

        var segmentPaths = DiscoverSegments();
        if (segmentPaths.Count == 0)
        {
            // No WAL segments — void all remaining Pending entries
            registry.VoidRemainingPending();
            result.UowsVoided = registry.VoidEntryCount;
            result.ElapsedMicroseconds = ElapsedUs(startTicks);
            return result;
        }

        // ═══════════════════════════════════════════════════════════
        // Phase 2: Scan records, build committed set + collect FPIs
        // ═══════════════════════════════════════════════════════════

        var uowStates = new Dictionary<ushort, UowScanState>();
        var fpiMap = new Dictionary<int, FpiScanEntry>();

        using var reader = new WalSegmentReader(_fileIO);

        foreach (var segmentPath in segmentPaths)
        {
            if (!reader.OpenSegment(segmentPath))
            {
                continue; // Invalid segment header — skip
            }

            result.SegmentsScanned++;

            while (reader.TryReadNext(out var chunkHeader, out var body))
            {
                result.RecordsScanned++;

                switch ((WalChunkType)chunkHeader.ChunkType)
                {
                    case WalChunkType.FullPageImage:
                        CollectFpiChunk(fpiMap, body);
                        break;

                    case WalChunkType.Transaction:
                        ProcessTransactionChunk(uowStates, body, checkpointLSN);
                        break;

                    default:
                        // Unknown chunk type — skip (forward compatibility via ChunkSize)
                        break;
                }
            }

            if (reader.WasTruncated)
            {
                break; // Stop at truncation point
            }
        }

        result.LastValidLSN = reader.LastValidLSN;

        // ═══════════════════════════════════════════════════════════
        // Phase 3: Cross-reference with registry
        // ═══════════════════════════════════════════════════════════

        foreach (var kvp in uowStates)
        {
            if (kvp.Value.HasCommit)
            {
                registry.PromoteToWalDurable(kvp.Key);
                result.UowsPromoted++;
            }
        }

        // Void all remaining Pending entries
        var voidCountBefore = registry.VoidEntryCount;
        registry.VoidRemainingPending();
        result.UowsVoided = registry.VoidEntryCount - voidCountBefore;

        // ═══════════════════════════════════════════════════════════
        // Phase 4: FPI torn-page repair (BEFORE replay)
        // ═══════════════════════════════════════════════════════════

        if (_mmf != null && fpiMap.Count > 0)
        {
            var pageBuffer = new byte[PagedMMF.PageSize];
            foreach (var kvp in fpiMap)
            {
                var filePageIndex = kvp.Key;
                var fpiEntry = kvp.Value;

                // Read the current page from disk
                _mmf.ReadPageDirect(filePageIndex, pageBuffer);

                // Read stored CRC; if 0 the page was never checkpointed — skip
                var storedCrc = MemoryMarshal.Read<uint>(pageBuffer.AsSpan(PageBaseHeader.PageChecksumOffset));
                if (storedCrc == 0)
                {
                    continue;
                }

                // Compute CRC; if it matches the page is consistent — skip
                var computedCrc = WalCrc.ComputeSkipping(pageBuffer, PageBaseHeader.PageChecksumOffset, PageBaseHeader.PageChecksumSize);
                if (computedCrc == storedCrc)
                {
                    continue;
                }

                // CRC mismatch — page is torn, restore from FPI
                _mmf.WritePageDirect(filePageIndex, fpiEntry.PageData);
                result.FpiRecordsApplied++;
            }

            if (result.FpiRecordsApplied > 0)
            {
                _mmf.FlushToDisk();
            }
        }

        // ═══════════════════════════════════════════════════════════
        // Phase 5: Replay committed records
        // ═══════════════════════════════════════════════════════════

        if (dbe != null)
        {
            foreach (var kvp in uowStates)
            {
                if (!kvp.Value.HasCommit)
                {
                    continue; // Skip voided UoWs
                }

                // Replay records in LSN order
                foreach (var (recordHeader, recordPayload) in kvp.Value.Records.OrderBy(r => r.Header.LSN))
                {
                    var header = recordHeader;
                    WalReplayHelper.ReplayRecord(dbe, ref header, recordPayload);
                    result.RecordsReplayed++;
                }
            }
        }

        // ═══════════════════════════════════════════════════════════
        // Phase 6: Finalize
        // ═══════════════════════════════════════════════════════════

        result.ElapsedMicroseconds = ElapsedUs(startTicks);
        return result;
    }

    /// <summary>
    /// Processes a Transaction chunk body: parses the WalRecordHeader, extracts payload,
    /// groups by UowEpoch, and tracks UowBegin/UowCommit flags.
    /// </summary>
    private static void ProcessTransactionChunk(Dictionary<ushort, UowScanState> uowStates, ReadOnlySpan<byte> body, long checkpointLSN)
    {
        if (body.Length < WalRecordHeader.SizeInBytes)
        {
            return; // Malformed transaction chunk — skip
        }

        var header = MemoryMarshal.Read<WalRecordHeader>(body);
        var payload = body.Length > WalRecordHeader.SizeInBytes ? 
            body.Slice(WalRecordHeader.SizeInBytes, Math.Min(header.PayloadLength, body.Length - WalRecordHeader.SizeInBytes)) : ReadOnlySpan<byte>.Empty;

        var uowId = header.UowEpoch;
        if (uowId == 0)
        {
            return; // Skip records with no UoW association
        }

        // Skip records before checkpoint LSN
        if (checkpointLSN > 0 && header.LSN <= checkpointLSN)
        {
            return;
        }

        if (!uowStates.TryGetValue(uowId, out var state))
        {
            state = new UowScanState();
            uowStates[uowId] = state;
        }

        if ((header.Flags & (byte)WalRecordFlags.UowBegin) != 0)
        {
            state.HasBegin = true;
        }

        if ((header.Flags & (byte)WalRecordFlags.UowCommit) != 0)
        {
            state.HasCommit = true;
        }

        // Buffer the record for potential replay
        state.Records.Add((header, payload.ToArray()));
    }

    /// <summary>
    /// Collects an FPI chunk body into the map, keeping only the highest-LSN entry per file page index.
    /// FPI body layout: [LSN (8B)] [FpiMetadata (16B)] [page data (variable)].
    /// Handles both compressed (LZ4) and uncompressed FPI payloads.
    /// </summary>
    private static void CollectFpiChunk(Dictionary<int, FpiScanEntry> fpiMap, ReadOnlySpan<byte> body)
    {
        // Minimum body: LSN (8) + FpiMetadata (16) = 24 bytes
        if (body.Length < sizeof(long) + FpiMetadata.SizeInBytes)
        {
            return; // Malformed FPI chunk — skip
        }

        var lsn = MemoryMarshal.Read<long>(body);
        var meta = MemoryMarshal.Read<FpiMetadata>(body.Slice(sizeof(long)));
        var pagePayload = body.Slice(sizeof(long) + FpiMetadata.SizeInBytes);

        byte[] pageData;
        if (meta.CompressionAlgo != FpiCompression.AlgoNone)
        {
            // Compressed FPI — decompress the page payload
            if (meta.CompressionAlgo != FpiCompression.AlgoLZ4)
            {
                return; // Unknown compression algorithm — skip
            }

            pageData = new byte[meta.UncompressedSize];
            var decompressedSize = FpiCompression.Decompress(pagePayload, pageData);
            if (decompressedSize != meta.UncompressedSize)
            {
                return; // Decompression failure — skip
            }
        }
        else
        {
            // Uncompressed FPI — validate size and extract
            if (pagePayload.Length < PagedMMF.PageSize)
            {
                return; // Malformed — skip
            }

            pageData = pagePayload.Slice(0, PagedMMF.PageSize).ToArray();
        }

        if (fpiMap.TryGetValue(meta.FilePageIndex, out var existing))
        {
            // Keep only the most recent FPI (highest LSN)
            if (lsn > existing.LSN)
            {
                existing.LSN = lsn;
                existing.PageData = pageData;
                existing.Metadata = meta;
            }
        }
        else
        {
            fpiMap[meta.FilePageIndex] = new FpiScanEntry
            {
                LSN = lsn,
                PageData = pageData,
                Metadata = meta,
            };
        }
    }

    /// <summary>
    /// Discovers WAL segment files in the WAL directory, sorted by segment ID ascending.
    /// </summary>
    private List<string> DiscoverSegments()
    {
        if (!Directory.Exists(_walDirectory))
        {
            return [];
        }

        var walFiles = Directory.GetFiles(_walDirectory, "*.wal");
        if (walFiles.Length == 0)
        {
            return [];
        }

        // Sort by segment ID (filename is {segmentId:D16}.wal)
        Array.Sort(walFiles, (a, b) =>
        {
            var aId = ParseSegmentId(a);
            var bId = ParseSegmentId(b);
            return aId.CompareTo(bId);
        });

        return [.. walFiles];
    }

    /// <summary>
    /// Parses the segment ID from a WAL file path. Format: {segmentId:D16}.wal
    /// </summary>
    private static long ParseSegmentId(string path)
    {
        var fileName = Path.GetFileNameWithoutExtension(path);
        if (long.TryParse(fileName, out var segmentId))
        {
            return segmentId;
        }

        return long.MaxValue; // Unknown format — sort to end
    }

    private static long ElapsedUs(long startTicks) => (Stopwatch.GetTimestamp() - startTicks) * 1_000_000 / Stopwatch.Frequency;

    public void Dispose()
    {
        // No persistent resources to dispose
    }
}
