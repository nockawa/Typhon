using JetBrains.Annotations;
using System;
using System.Collections.Generic;
using System.Linq;

namespace Typhon.Engine;

/// <summary>
/// Immutable snapshot of all resource metrics at a point in time.
/// </summary>
/// <remarks>
/// <para>
/// A snapshot provides a consistent-enough reading of all metric values across the resource tree.
/// </para>
/// <list type="bullet">
/// <item><description><b>Per-node atomic</b>: Each node's ReadMetrics() reads all fields together</description></item>
/// <item><description><b>Cross-node approximate</b>: Different nodes may be read microseconds apart</description></item>
/// <item><description><b>No global lock</b>: Tree traversal doesn't block other threads</description></item>
/// </list>
/// <para>
/// Query methods operate on the frozen data, making them safe to call from any thread.
/// </para>
/// </remarks>
[PublicAPI]
public sealed class ResourceSnapshot
{
    /// <summary>
    /// When this snapshot was taken.
    /// </summary>
    public DateTime Timestamp { get; init; }

    /// <summary>
    /// All nodes with their metric values, keyed by node path.
    /// </summary>
    /// <example>
    /// <code>
    /// var utilization = snapshot.Nodes["Storage/PageCache"].Capacity?.Utilization;
    /// </code>
    /// </example>
    public IReadOnlyDictionary<string, NodeSnapshot> Nodes { get; init; }

    /// <summary>
    /// Throughput rates (ops/sec) computed from the previous snapshot.
    /// Null for the first snapshot (no previous to compare against).
    /// </summary>
    /// <example>
    /// <code>
    /// var hitRate = snapshot.Rates?["Storage/PageCache"]["CacheHits"];
    /// </code>
    /// </example>
    public ThroughputRates Rates { get; init; }

    /// <summary>
    /// Sum <see cref="MemoryMetrics.AllocatedBytes"/> across all descendants of the given node.
    /// </summary>
    /// <param name="nodePath">Path to the subtree root (e.g., "DataEngine").</param>
    /// <returns>Total bytes allocated by all nodes under the path.</returns>
    /// <remarks>
    /// <para>
    /// Useful for memory attribution: "How much does each subsystem use?"
    /// </para>
    /// <example>
    /// <code>
    /// var dataEngineMemory = snapshot.GetSubtreeMemory("DataEngine");
    /// var storageMemory = snapshot.GetSubtreeMemory("Storage");
    /// </code>
    /// </example>
    /// </remarks>
    public long GetSubtreeMemory(string nodePath) =>
        Nodes.Values
            .Where(n => n.Path == nodePath || n.Path.StartsWith(nodePath + "/"))
            .Where(n => n.Memory.HasValue)
            .Sum(n => n.Memory.Value.AllocatedBytes);

    /// <summary>
    /// Find the node with highest <see cref="CapacityMetrics.Utilization"/> in the tree.
    /// </summary>
    /// <returns>The most utilized node, or null if no nodes have Capacity metrics.</returns>
    /// <remarks>
    /// <para>
    /// Useful for finding the bottleneck: "Which resource is about to run out?"
    /// </para>
    /// </remarks>
    public NodeSnapshot FindMostUtilized() =>
        Nodes.Values
            .Where(n => n.Capacity.HasValue)
            .OrderByDescending(n => n.Capacity.Value.Utilization)
            .FirstOrDefault();

    /// <summary>
    /// Find the node with highest <see cref="CapacityMetrics.Utilization"/> above a threshold.
    /// </summary>
    /// <param name="threshold">Minimum utilization (0.0 to 1.0) to include in results.</param>
    /// <returns>Nodes above the threshold, sorted by utilization descending.</returns>
    public IEnumerable<NodeSnapshot> FindMostUtilized(double threshold) =>
        Nodes.Values
            .Where(n => n.Capacity.HasValue)
            .Where(n => n.Capacity.Value.Utilization >= threshold)
            .OrderByDescending(n => n.Capacity.Value.Utilization);

    /// <summary>
    /// Find nodes where <see cref="ContentionMetrics.WaitCount"/> &gt; 0, sorted by TotalWaitUs descending.
    /// </summary>
    /// <returns>Nodes with contention, hottest first.</returns>
    /// <remarks>
    /// <para>
    /// Useful for diagnosing concurrency issues: "Where are threads waiting?"
    /// </para>
    /// </remarks>
    public IEnumerable<NodeSnapshot> FindContentionHotspots() =>
        Nodes.Values
            .Where(n => n.Contention.HasValue)
            .Where(n => n.Contention.Value.WaitCount > 0)
            .OrderByDescending(n => n.Contention.Value.TotalWaitUs);

    /// <summary>
    /// Find nodes with contention where wait time exceeds a threshold.
    /// </summary>
    /// <param name="minWaitUs">Minimum total wait time in microseconds.</param>
    /// <returns>Nodes with total wait time exceeding the threshold, sorted descending.</returns>
    public IEnumerable<NodeSnapshot> FindContentionHotspots(long minWaitUs) =>
        Nodes.Values
            .Where(n => n.Contention.HasValue)
            .Where(n => n.Contention.Value.TotalWaitUs >= minWaitUs)
            .OrderByDescending(n => n.Contention.Value.TotalWaitUs);

    /// <summary>
    /// Get a specific node by path.
    /// </summary>
    /// <param name="nodePath">Path to the node.</param>
    /// <returns>The node snapshot, or null if not found.</returns>
    public NodeSnapshot GetNode(string nodePath) => Nodes.TryGetValue(nodePath, out var node) ? node : null;

    /// <summary>
    /// Find all nodes of a specific type.
    /// </summary>
    /// <param name="type">The resource type to find.</param>
    /// <returns>All nodes of the specified type.</returns>
    public IEnumerable<NodeSnapshot> FindByType(ResourceType type) => Nodes.Values.Where(n => n.Type == type);

    /// <summary>
    /// Get all nodes under a subtree path.
    /// </summary>
    /// <param name="nodePath">Path to the subtree root.</param>
    /// <returns>All nodes under the path, including the root itself.</returns>
    public IEnumerable<NodeSnapshot> GetSubtree(string nodePath) =>
        Nodes.Values
            .Where(n => n.Path == nodePath || n.Path.StartsWith(nodePath + "/"));
}
