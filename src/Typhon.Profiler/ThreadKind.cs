namespace Typhon.Profiler;

/// <summary>
/// Categorizes a producer thread for the viewer's filter UI. Emitted as a 1-byte field on
/// <see cref="TraceEventKind.ThreadInfo"/> records so the viewer can split threads into Main / Workers / Other
/// groups in its lane filter without name pattern-matching on the client side.
/// </summary>
public enum ThreadKind : byte
{
    /// <summary>Host's bootstrap thread — the one that called <c>TyphonProfiler.Start()</c>.</summary>
    Main = 0,

    /// <summary>DAG-scheduler worker thread (named <c>Typhon.Worker-N</c>).</summary>
    Worker = 1,

    /// <summary>.NET ThreadPool callback (I/O completion, async continuation).</summary>
    Pool = 2,

    /// <summary>Anything else — long-lived utility threads (profiler consumer, GC ingestion, custom timer threads).</summary>
    Other = 3,
}
