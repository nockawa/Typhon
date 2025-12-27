using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics.Metrics;
using System.Linq;

namespace Typhon.Engine.Misc;

/// <summary>
/// Metadata for a registered resource, combining hierarchical organization
/// with OpenTelemetry instrumentation.
/// </summary>
public record ResourceMetadata
{
    /// <summary>
    /// Simple name of this resource (e.g., "PageCache")
    /// </summary>
    public required string Name { get; init; }

    /// <summary>
    /// Full hierarchical path (e.g., "DatabaseEngine/Persistence/PageCache")
    /// </summary>
    public required string FullPath { get; init; }

    /// <summary>
    /// When this resource was registered
    /// </summary>
    public DateTime RegisteredAt { get; init; } = DateTime.UtcNow;

    /// <summary>
    /// Type classification (e.g., "ComponentTable", "Cache", "Index")
    /// </summary>
    public string ResourceType { get; init; }

    /// <summary>
    /// Static properties for categorization and filtering
    /// (e.g., component type, capacity, configuration)
    /// </summary>
    public Dictionary<string, object> Tags { get; init; } = new();

    /// <summary>
    /// Optional: OpenTelemetry Meter for this resource.
    /// Use this to create Counters, Gauges, Histograms, etc.
    /// </summary>
    public Meter Meter { get; init; }

    /// <summary>
    /// Debug-only properties that can be queried at runtime.
    /// These are NOT exported to telemetry (use Meter instruments for that).
    /// Useful for console dumps, admin UIs, or test assertions.
    /// </summary>
    public Dictionary<string, Func<object>> DebugProperties { get; init; } = new();
}

/// <summary>
/// Node in the hierarchical resource tree
/// </summary>
public class ResourceNode
{
    public string Name { get; }
    public ResourceMetadata Metadata { get; }
    public ResourceNode Parent { get; }
    public ConcurrentDictionary<string, ResourceNode> Children { get; }

    public ResourceNode(string name, ResourceMetadata metadata, ResourceNode parent)
    {
        Name = name;
        Metadata = metadata;
        Parent = parent;
        Children = new ConcurrentDictionary<string, ResourceNode>(StringComparer.OrdinalIgnoreCase);
    }

    public string GetFullPath()
    {
        if (Parent == null || Parent.Parent == null) // Root or direct child of root
            return Name;

        var parts = new List<string>();
        var current = this;
        while (current?.Parent?.Parent != null) // Stop before root
        {
            parts.Add(current.Name);
            current = current.Parent;
        }
        parts.Reverse();
        return string.Join("/", parts);
    }
}

/// <summary>
/// Central registry for all engine resources, providing:
/// 1. Hierarchical organization (path-based lookup)
/// 2. Runtime inspection (debug properties)
/// 3. OpenTelemetry integration (Meter per resource)
/// </summary>
public interface IResourceRegistry
{
    /// <summary>
    /// Register a resource at the specified path.
    /// Path segments are separated by '/' (e.g., "Engine/ComponentTables/PlayerData")
    /// </summary>
    void Register(string path, ResourceMetadata metadata);

    /// <summary>
    /// Register a resource as a child of an existing parent path
    /// </summary>
    void Register(string parentPath, string name, ResourceMetadata metadata);

    /// <summary>
    /// Remove a resource and all its descendants
    /// </summary>
    void Unregister(string path);

    /// <summary>
    /// Get metadata for a specific resource
    /// </summary>
    ResourceMetadata GetResource(string path);

    /// <summary>
    /// Get all registered resources (breadth-first traversal)
    /// </summary>
    IEnumerable<ResourceMetadata> GetAllResources();

    /// <summary>
    /// Get all descendants of a resource (useful for querying subtrees)
    /// </summary>
    IEnumerable<ResourceMetadata> GetDescendants(string path);

    /// <summary>
    /// Get direct children of a resource
    /// </summary>
    IEnumerable<ResourceMetadata> GetChildren(string path);

    /// <summary>
    /// Collect current debug properties from all resources.
    /// This executes all DebugProperties delegates and returns a snapshot.
    /// </summary>
    Dictionary<string, Dictionary<string, object>> CollectDebugSnapshot();
}

public class ResourceRegistry : IResourceRegistry
{
    private readonly ResourceNode _root;

    public ResourceRegistry()
    {
        _root = new ResourceNode("Root", null, null);
    }

    public void Register(string path, ResourceMetadata metadata)
    {
        if (string.IsNullOrWhiteSpace(path))
            throw new ArgumentException("Path cannot be empty", nameof(path));

        var segments = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
        var current = _root;

        // Navigate/create intermediate nodes
        for (int i = 0; i < segments.Length - 1; i++)
        {
            current = current.Children.GetOrAdd(segments[i],
                name => new ResourceNode(name, null, current));
        }

        // Create final node with metadata
        var finalName = segments[^1];
        var node = new ResourceNode(finalName, metadata, current);

        if (!current.Children.TryAdd(finalName, node))
        {
            throw new InvalidOperationException($"Resource already registered at path: {path}");
        }
    }

    public void Register(string parentPath, string name, ResourceMetadata metadata)
    {
        var fullPath = string.IsNullOrWhiteSpace(parentPath)
            ? name
            : $"{parentPath}/{name}";
        Register(fullPath, metadata);
    }

    public void Unregister(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return;

        var segments = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
        var current = _root;

        // Navigate to parent
        for (int i = 0; i < segments.Length - 1; i++)
        {
            if (!current.Children.TryGetValue(segments[i], out var next))
                return; // Path doesn't exist
            current = next;
        }

        // Remove final segment
        current.Children.TryRemove(segments[^1], out _);
    }

    public ResourceMetadata GetResource(string path)
    {
        var node = FindNode(path);
        return node?.Metadata;
    }

    public IEnumerable<ResourceMetadata> GetAllResources()
    {
        return EnumerateDescendants(_root)
            .Where(n => n.Metadata != null)
            .Select(n => n.Metadata!);
    }

    public IEnumerable<ResourceMetadata> GetDescendants(string path)
    {
        var node = FindNode(path);
        if (node == null)
            return Enumerable.Empty<ResourceMetadata>();

        return EnumerateDescendants(node)
            .Where(n => n.Metadata != null && n != node)
            .Select(n => n.Metadata!);
    }

    public IEnumerable<ResourceMetadata> GetChildren(string path)
    {
        var node = FindNode(path);
        if (node == null)
            return Enumerable.Empty<ResourceMetadata>();

        return node.Children.Values
            .Where(n => n.Metadata != null)
            .Select(n => n.Metadata!);
    }

    public Dictionary<string, Dictionary<string, object>> CollectDebugSnapshot()
    {
        var snapshot = new Dictionary<string, Dictionary<string, object>>();

        foreach (var resource in GetAllResources())
        {
            if (resource.DebugProperties.Count == 0)
                continue;

            var properties = new Dictionary<string, object>();
            foreach (var (key, valueFunc) in resource.DebugProperties)
            {
                try
                {
                    properties[key] = valueFunc();
                }
                catch (Exception ex)
                {
                    properties[key] = $"<Error: {ex.Message}>";
                }
            }

            snapshot[resource.FullPath] = properties;
        }

        return snapshot;
    }

    private ResourceNode FindNode(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return null;

        var segments = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
        var current = _root;

        foreach (var segment in segments)
        {
            if (!current.Children.TryGetValue(segment, out var next))
                return null;
            current = next;
        }

        return current;
    }

    private IEnumerable<ResourceNode> EnumerateDescendants(ResourceNode root)
    {
        var queue = new Queue<ResourceNode>();
        queue.Enqueue(root);

        while (queue.Count > 0)
        {
            var node = queue.Dequeue();
            yield return node;

            foreach (var child in node.Children.Values)
                queue.Enqueue(child);
        }
    }
}
