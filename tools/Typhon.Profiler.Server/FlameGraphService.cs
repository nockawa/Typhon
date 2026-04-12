using Microsoft.Diagnostics.Tracing.Etlx;

namespace Typhon.Profiler.Server;

/// <summary>
/// Parses a <c>.nettrace</c> file and builds flame graph data by correlating
/// CPU sample timestamps with trace event time ranges.
/// </summary>
public static class FlameGraphService
{
    public class FlameNode
    {
        public string Name { get; set; } = "";
        public int TotalSamples { get; set; }
        public int SelfSamples { get; set; }
        public Dictionary<string, FlameNode> Children { get; set; } = new();
    }

    /// <summary>
    /// Builds a flame graph from CPU samples in a <c>.nettrace</c> file.
    /// </summary>
    /// <param name="nettracePath">Path to the <c>.nettrace</c> file.</param>
    /// <param name="samplingSessionStartQpc">QPC timestamp when EventPipe session started (from trace file header).</param>
    /// <param name="timestampFrequency">Stopwatch.Frequency from trace file header.</param>
    /// <param name="fromUs">Start of time range in absolute µs (same time base as .typhon-trace).</param>
    /// <param name="toUs">End of time range in absolute µs.</param>
    /// <param name="threadId">Optional: filter to a specific OS thread ID. -1 = all threads.</param>
    public static (FlameNode root, int totalSamples) Build(
        string nettracePath,
        long samplingSessionStartQpc,
        long timestampFrequency,
        double fromUs,
        double toUs,
        int threadId = -1)
    {
        var root = new FlameNode { Name = "(root)" };
        int totalSamples = 0;

        if (!File.Exists(nettracePath))
        {
            return (root, 0);
        }

        // Convert .nettrace → TraceLog (.etlx) to get resolved call stacks
        var etlxPath = Path.ChangeExtension(nettracePath, ".etlx");
        try { File.Delete(etlxPath); } catch { }

        var options = new TraceLogOptions { ContinueOnError = true };
        try
        {
            TraceLog.CreateFromEventPipeDataFile(nettracePath, etlxPath, options);
        }
        catch
        {
            // Truncated .nettrace — partial conversion may still be usable
        }

        if (!File.Exists(etlxPath))
        {
            return (root, 0);
        }

        using var traceLog = new TraceLog(etlxPath);

        // Convert EventPipe relative ms → absolute µs (same time base as .typhon-trace)
        // eventAbsoluteUs = sessionStartUs + eventRelativeMs * 1000
        double sessionStartUs = samplingSessionStartQpc > 0 && timestampFrequency > 0
            ? (double)samplingSessionStartQpc / timestampFrequency * 1_000_000.0
            : 0;

        bool hasTimeFilter = samplingSessionStartQpc > 0 && fromUs > 0;

        foreach (var evt in traceLog.Events)
        {
            // Filter by thread if specified
            if (threadId >= 0 && evt.ThreadID != threadId)
            {
                continue;
            }

            // Convert to absolute µs and filter by time range
            if (hasTimeFilter)
            {
                double eventAbsoluteUs = sessionStartUs + evt.TimeStampRelativeMSec * 1000.0;
                if (eventAbsoluteUs < fromUs || eventAbsoluteUs > toUs)
                {
                    continue;
                }
            }

            // Get call stack
            var callStack = evt.CallStack();
            if (callStack == null)
            {
                continue;
            }

            // Walk the stack from leaf to root
            var frames = new List<string>();
            for (var frame = callStack; frame != null; frame = frame.Caller)
            {
                var name = frame.CodeAddress?.FullMethodName;
                if (string.IsNullOrEmpty(name))
                {
                    name = $"0x{frame.CodeAddress?.Address:x}";
                }

                frames.Add(name);
            }

            frames.Reverse();

            if (frames.Count == 0)
            {
                continue;
            }

            totalSamples++;
            root.TotalSamples++;

            var current = root;
            foreach (var frame in frames)
            {
                if (!current.Children.TryGetValue(frame, out var child))
                {
                    child = new FlameNode { Name = frame };
                    current.Children[frame] = child;
                }

                child.TotalSamples++;
                current = child;
            }

            current.SelfSamples++;
        }

        try { File.Delete(etlxPath); } catch { }

        return (root, totalSamples);
    }

    public static object ToSerializable(FlameNode node, int minSamples = 0)
    {
        var children = new List<object>();
        foreach (var child in node.Children.Values.OrderByDescending(c => c.TotalSamples))
        {
            if (child.TotalSamples >= minSamples)
            {
                children.Add(ToSerializable(child, minSamples));
            }
        }

        return new
        {
            name = node.Name,
            total = node.TotalSamples,
            self = node.SelfSamples,
            children
        };
    }
}
