using System;
using System.Collections.Generic;

namespace Typhon.Engine;

/// <summary>
/// Derives DAG edges from declared system access (RFC 07 — Unit 3). Validates that conflicts within a phase are either explicitly resolved
/// (via <c>.After()</c>/<c>.Before()</c>) or expressed declaratively (<see cref="SystemBuilder.ReadsFresh{T}"/> / <see cref="SystemBuilder.ReadsSnapshot{T}"/>).
/// </summary>
/// <remarks>
/// <para>
/// Inputs: per-system info (name, resolved phase index, access descriptor) + the explicit edges already declared via <c>.After()</c>, <c>.Before()</c>, and
/// <c>.AfterAll()</c>. Output: a list of derived edges to add to the DAG.
/// </para>
/// <para>
/// Conflict detection (hard errors at <c>Build()</c>):
/// <list type="bullet">
///   <item>W×W same phase, no explicit ordering between the writers.</item>
///   <item>R×W plain (<c>b.Reads&lt;T&gt;()</c>) with a same-phase writer of T — must upgrade to <c>ReadsFresh</c> or <c>ReadsSnapshot</c>.</item>
///   <item>Resource W×W same phase, no explicit ordering.</item>
///   <item><c>ExclusivePhase()</c> declared on a system that shares its phase with another system.</item>
/// </list>
/// </para>
/// <para>
/// Edge derivation:
/// <list type="bullet">
///   <item>R×W fresh: writer → reader (reader sees this-tick value).</item>
///   <item>R×W snapshot: reader → writer (reader sees previous-tick value, parallelism enabled).</item>
///   <item>Event producer → consumer in same phase.</item>
///   <item>Resource R×W: writer → reader if both fresh-style is implicit (reader after writer). Resource snapshot semantics not yet expressed.</item>
///   <item>Cross-phase: every system in phase N depends on every system in phase N-1 (transitivity covers non-adjacent pairs).</item>
/// </list>
/// </para>
/// <para>
/// Systems with <c>PhaseIndex == -1</c> (no phase declared) and no access declarations are ignored — they participate in the DAG only via explicit edges.
/// This preserves backwards compatibility during the migration window.
/// </para>
/// </remarks>
internal static class AccessDagDeriver
{
    /// <summary>Lightweight view of a system passed to the deriver. Avoids leaking <c>SystemRegistration</c> outside <see cref="RuntimeSchedule"/>.</summary>
    public readonly struct SystemInfo
    {
        public readonly string Name;
        public readonly int PhaseIndex;
        public readonly SystemAccessDescriptor Access;

        public SystemInfo(string name, int phaseIndex, SystemAccessDescriptor access)
        {
            Name = name;
            PhaseIndex = phaseIndex;
            Access = access;
        }
    }

    /// <summary>
    /// Validates declared access and derives DAG edges. Returns the list of edges to add to the DAG.
    /// Throws <see cref="InvalidOperationException"/> on conflict with a copy-paste-ready suggestion.
    /// </summary>
    public static List<(string From, string To)> DeriveAndValidate(IReadOnlyList<SystemInfo> systems, IReadOnlyList<(string From, string To)> explicitEdges)
    {
        var derived = new List<(string From, string To)>();

        // Build lookup: explicit direct edges (used to check W×W resolution).
        // We use direct adjacency only — transitive reachability is not checked. If transitive ordering is needed, the user should add an explicit edge.
        var explicitAdjacency = new HashSet<(string, string)>();
        foreach (var (from, to) in explicitEdges)
        {
            explicitAdjacency.Add((from, to));
        }

        // Group systems by phase. PhaseIndex == -1 is the "no phase" sentinel — should be unreachable post-Unit-5 (RuntimeSchedule.Build
        // assigns RuntimeOptions.DefaultPhase to undeclared registrations) but kept as a defensive skip for systems built outside the
        // RuntimeSchedule path.
        var byPhase = new Dictionary<int, List<SystemInfo>>();
        foreach (var sys in systems)
        {
            if (sys.PhaseIndex < 0)
            {
                continue;
            }

            if (!byPhase.TryGetValue(sys.PhaseIndex, out var list))
            {
                list = [];
                byPhase[sys.PhaseIndex] = list;
            }

            list.Add(sys);
        }

        // ── Per-phase conflict detection + intra-phase edge derivation ─────
        foreach (var (phaseIdx, phaseSystems) in byPhase)
        {
            DerivePhase(phaseIdx, phaseSystems, explicitAdjacency, derived);
        }

        // ── Cross-phase edges: chain consecutive populated phases ──
        // Empty phases between two populated ones don't break the chain — we link them directly. Transitivity handles further pairs.
        var populatedPhaseIndices = new List<int>(byPhase.Keys);
        populatedPhaseIndices.Sort();
        for (var p = 0; p < populatedPhaseIndices.Count - 1; p++)
        {
            var fromList = byPhase[populatedPhaseIndices[p]];
            var toList = byPhase[populatedPhaseIndices[p + 1]];

            foreach (var fromSys in fromList)
            {
                foreach (var toSys in toList)
                {
                    derived.Add((fromSys.Name, toSys.Name));
                }
            }
        }

        return derived;
    }

    private static void DerivePhase(int phaseIdx, List<SystemInfo> phaseSystems, HashSet<(string, string)> explicitAdjacency, List<(string From, string To)> derived)
    {
        // ── ExclusivePhase enforcement ──
        SystemInfo? exclusive = null;
        foreach (var sys in phaseSystems)
        {
            if (sys.Access != null && sys.Access.ExclusivePhase)
            {
                if (exclusive.HasValue)
                {
                    throw new InvalidOperationException(
                        $"Multiple systems declare ExclusivePhase() in the same phase (index {phaseIdx}): " +
                        $"'{exclusive.Value.Name}' and '{sys.Name}'. Only one system may claim exclusivity per phase.");
                }

                if (phaseSystems.Count > 1)
                {
                    throw new InvalidOperationException(
                        $"System '{sys.Name}' declared ExclusivePhase() but other systems share its phase (index {phaseIdx}). " +
                        "Either move the other systems to a different phase, or remove ExclusivePhase() from this system.");
                }

                exclusive = sys;
            }
        }

        // ── Group access by component / event / resource ──
        var writers = new Dictionary<Type, List<SystemInfo>>();
        var readsPlain = new Dictionary<Type, List<SystemInfo>>();
        var readsFresh = new Dictionary<Type, List<SystemInfo>>();
        var readsSnapshot = new Dictionary<Type, List<SystemInfo>>();
        var eventProducers = new Dictionary<EventQueueBase, List<SystemInfo>>();
        var eventConsumers = new Dictionary<EventQueueBase, List<SystemInfo>>();
        var resourceWriters = new Dictionary<string, List<SystemInfo>>(StringComparer.Ordinal);
        var resourceReaders = new Dictionary<string, List<SystemInfo>>(StringComparer.Ordinal);

        foreach (var sys in phaseSystems)
        {
            var access = sys.Access;
            if (access == null)
            {
                continue;
            }

            foreach (var t in access.Writes)
            {
                Bucket(writers, t, sys);
            }

            foreach (var t in access.Reads)
            {
                Bucket(readsPlain, t, sys);
            }

            foreach (var t in access.ReadsFresh)
            {
                Bucket(readsFresh, t, sys);
            }

            foreach (var t in access.ReadsSnapshot)
            {
                Bucket(readsSnapshot, t, sys);
            }

            foreach (var q in access.WritesEvents)
            {
                Bucket(eventProducers, q, sys);
            }

            foreach (var q in access.ReadsEvents)
            {
                Bucket(eventConsumers, q, sys);
            }

            foreach (var r in access.WritesResources)
            {
                Bucket(resourceWriters, r, sys);
            }

            foreach (var r in access.ReadsResources)
            {
                Bucket(resourceReaders, r, sys);
            }
        }

        // ── W×W: hard error unless EXACTLY one explicit direction is declared ──
        // We require XOR not OR: declaring both `(a→b)` and `(b→a)` produces a cycle, and the user's intent is genuinely
        // ambiguous (the cycle detector would later complain anyway with a less-specific message).
        // Limitation: only direct adjacency is consulted — transitive reachability is not. With 3+ writers of the same component,
        // each pair must be directly ordered. A linear chain `A.Before(B).Before(C)` does not implicitly resolve `(A,C)`.
        foreach (var (compType, writerList) in writers)
        {
            if (writerList.Count <= 1)
            {
                continue;
            }

            for (var i = 0; i < writerList.Count; i++)
            {
                for (var j = i + 1; j < writerList.Count; j++)
                {
                    var a = writerList[i];
                    var b = writerList[j];

                    var hasAB = explicitAdjacency.Contains((a.Name, b.Name));
                    var hasBA = explicitAdjacency.Contains((b.Name, a.Name));

                    if (hasAB && hasBA)
                    {
                        throw new InvalidOperationException(
                            $"Systems '{a.Name}' and '{b.Name}' both declare Writes<{compType.Name}> in phase index {phaseIdx} AND have explicit edges in both directions " +
                            "(would form a cycle). Pick one direction: either `.After(...)` or `.Before(...)`, not both.");
                    }

                    if (!hasAB && !hasBA)
                    {
                        throw new InvalidOperationException(
                            $"Systems '{a.Name}' and '{b.Name}' both declare Writes<{compType.Name}> in phase index {phaseIdx}. " +
                            $"Resolution: add `.After(\"{a.Name}\")` on `{b.Name}`, or `.Before(\"{b.Name}\")` on `{a.Name}`, or move one system to a different phase.");
                    }
                }
            }
        }

        // ── R×W plain: hard error if same-phase writer exists ──
        foreach (var (compType, readers) in readsPlain)
        {
            if (!writers.TryGetValue(compType, out var writerList) || writerList.Count == 0)
            {
                continue;
            }

            var writerName = writerList[0].Name;
            var reader = readers[0];

            throw new InvalidOperationException(
                $"System '{reader.Name}' declares Reads<{compType.Name}> in phase index {phaseIdx}, but system '{writerName}' writes the same component in this phase. " +
                $"Upgrade the read to `ReadsFresh<{compType.Name}>()` (run after writers, see this-tick value) or `ReadsSnapshot<{compType.Name}>()` (run before writers, see previous-tick value).");
        }

        // ── R×W fresh: derive writer → reader edge ──
        foreach (var (compType, freshReaders) in readsFresh)
        {
            if (!writers.TryGetValue(compType, out var writerList))
            {
                continue;
            }

            foreach (var writer in writerList)
            {
                foreach (var reader in freshReaders)
                {
                    if (writer.Name == reader.Name)
                    {
                        continue;
                    }

                    derived.Add((writer.Name, reader.Name));
                }
            }
        }

        // ── R×W snapshot: derive reader → writer edge ──
        foreach (var (compType, snapshotReaders) in readsSnapshot)
        {
            if (!writers.TryGetValue(compType, out var writerList))
            {
                continue;
            }

            foreach (var reader in snapshotReaders)
            {
                foreach (var writer in writerList)
                {
                    if (writer.Name == reader.Name)
                    {
                        continue;
                    }

                    derived.Add((reader.Name, writer.Name));
                }
            }
        }

        // ── Event producer → consumer edges (same phase only — cross-phase handled by phase ordering) ──
        foreach (var (queue, producers) in eventProducers)
        {
            if (!eventConsumers.TryGetValue(queue, out var consumers))
            {
                continue;
            }

            foreach (var producer in producers)
            {
                foreach (var consumer in consumers)
                {
                    if (producer.Name == consumer.Name)
                    {
                        continue;
                    }

                    derived.Add((producer.Name, consumer.Name));
                }
            }
        }

        // ── Resource W×W: hard error unless EXACTLY one explicit direction is declared (same XOR rule as component W×W) ──
        foreach (var (resourceName, writerList) in resourceWriters)
        {
            if (writerList.Count <= 1)
            {
                continue;
            }

            for (var i = 0; i < writerList.Count; i++)
            {
                for (var j = i + 1; j < writerList.Count; j++)
                {
                    var a = writerList[i];
                    var b = writerList[j];

                    var hasAB = explicitAdjacency.Contains((a.Name, b.Name));
                    var hasBA = explicitAdjacency.Contains((b.Name, a.Name));

                    if (hasAB && hasBA)
                    {
                        throw new InvalidOperationException(
                            $"Systems '{a.Name}' and '{b.Name}' both declare WritesResource(\"{resourceName}\") in phase index {phaseIdx} AND have explicit edges in both directions " +
                            "(would form a cycle). Pick one direction: either `.After(...)` or `.Before(...)`, not both.");
                    }

                    if (!hasAB && !hasBA)
                    {
                        throw new InvalidOperationException(
                            $"Systems '{a.Name}' and '{b.Name}' both declare WritesResource(\"{resourceName}\") in phase index {phaseIdx}. " +
                            $"Resolution: add `.After(\"{a.Name}\")` on `{b.Name}`, or `.Before(\"{b.Name}\")` on `{a.Name}`, or move one system to a different phase.");
                    }
                }
            }
        }

        // ── Resource R×W: derive writer → reader edge (no Fresh/Snapshot distinction for resources in v1) ──
        foreach (var (resourceName, readers) in resourceReaders)
        {
            if (!resourceWriters.TryGetValue(resourceName, out var writerList))
            {
                continue;
            }

            foreach (var writer in writerList)
            {
                foreach (var reader in readers)
                {
                    if (writer.Name == reader.Name)
                    {
                        continue;
                    }

                    derived.Add((writer.Name, reader.Name));
                }
            }
        }
    }

    private static void Bucket<TKey>(Dictionary<TKey, List<SystemInfo>> map, TKey key, SystemInfo sys)
    {
        if (!map.TryGetValue(key, out var list))
        {
            list = [];
            map[key] = list;
        }

        list.Add(sys);
    }
}
