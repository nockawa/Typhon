using JetBrains.Annotations;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;

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
/// Orchestrates WAL crash recovery: scans WAL segments, identifies committed UoWs,
/// voids pending ones, and replays committed records to restore data consistency.
/// </summary>
internal sealed class WalRecovery : IDisposable
{
    private readonly IWalFileIO _fileIO;
    private readonly string _walDirectory;

    public WalRecovery(IWalFileIO fileIO, string walDirectory)
    {
        ArgumentNullException.ThrowIfNull(fileIO);
        ArgumentNullException.ThrowIfNull(walDirectory);
        _fileIO = fileIO;
        _walDirectory = walDirectory;
    }

    /// <summary>
    /// Runs the full 5-phase recovery algorithm.
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
        // Phase 2: Scan records, build committed set
        // ═══════════════════════════════════════════════════════════

        var uowStates = new Dictionary<ushort, UowScanState>();

        using var reader = new WalSegmentReader(_fileIO);

        foreach (var segmentPath in segmentPaths)
        {
            if (!reader.OpenSegment(segmentPath))
            {
                continue; // Invalid segment header — skip
            }

            result.SegmentsScanned++;

            while (reader.TryReadNext(out var header, out var payload))
            {
                result.RecordsScanned++;

                var uowId = header.UowEpoch;
                if (uowId == 0)
                {
                    continue; // Skip records with no UoW association
                }

                // Skip records before checkpoint LSN
                if (checkpointLSN > 0 && header.LSN <= checkpointLSN)
                {
                    continue;
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
        // Phase 4: Replay committed records
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

        // Phase 4b: FPI torn-page repair (deferred to #57 — requires checkpoint cycle tracking)
        // result.FpiRecordsApplied = 0;

        // ═══════════════════════════════════════════════════════════
        // Phase 5: Finalize
        // ═══════════════════════════════════════════════════════════

        result.ElapsedMicroseconds = ElapsedUs(startTicks);
        return result;
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
