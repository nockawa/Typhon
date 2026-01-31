using JetBrains.Annotations;
using System;
using System.Linq;

namespace Typhon.Engine;

/// <summary>
/// Configuration options for <see cref="ResourceRegistry"/>.
/// </summary>
[PublicAPI]
public class ResourceRegistryOptions
{
    /// <summary>Name for this registry instance (for diagnostics).</summary>
    public string Name { get; set; }
}

/// <summary>
/// Default implementation of <see cref="IResourceRegistry"/>.
/// Creates a hierarchical tree with four subsystem nodes under Root.
/// </summary>
/// <remarks>
/// Tree structure:
/// <code>
/// Root
/// ├── Storage/      (PageCache, ManagedPagedMMF)
/// ├── DataEngine/   (DatabaseEngine, ComponentTables)
/// ├── Durability/   (WAL, Checkpoint)
/// └── Allocation/   (MemoryAllocator, Bitmaps)
/// </code>
/// </remarks>
[PublicAPI]
public class ResourceRegistry : IResourceRegistry
{
    /// <summary>Name of this registry instance.</summary>
    public string Name { get; }

    /// <inheritdoc />
    public IResource Root { get; }

    /// <inheritdoc />
    public IResource Storage { get; }

    /// <inheritdoc />
    public IResource DataEngine { get; }

    /// <inheritdoc />
    public IResource Durability { get; }

    /// <inheritdoc />
    public IResource Allocation { get; }

    /// <summary>
    /// Creates a new resource registry with the standard subsystem tree.
    /// </summary>
    public ResourceRegistry(ResourceRegistryOptions options)
    {
        Name = options?.Name ?? "DefaultResourceRegistry";

        // Create root node
        Root = new ResourceNode("Root", ResourceType.Node, this);

        // Create subsystem nodes under root
        Storage = new ResourceNode("Storage", ResourceType.Node, Root);
        DataEngine = new ResourceNode("DataEngine", ResourceType.Node, Root);
        Durability = new ResourceNode("Durability", ResourceType.Node, Root);
        Allocation = new ResourceNode("Allocation", ResourceType.Node, Root);

        // Register subsystems as children of root
        Root.RegisterChild(Storage);
        Root.RegisterChild(DataEngine);
        Root.RegisterChild(Durability);
        Root.RegisterChild(Allocation);
    }

    /// <inheritdoc />
    public IResource GetSubsystem(ResourceSubsystem subsystem) => subsystem switch
    {
        ResourceSubsystem.Storage => Storage,
        ResourceSubsystem.DataEngine => DataEngine,
        ResourceSubsystem.Durability => Durability,
        ResourceSubsystem.Allocation => Allocation,
        _ => throw new ArgumentOutOfRangeException(nameof(subsystem), subsystem, "Unknown subsystem")
    };

    /// <inheritdoc />
    public IResource Register<T>(T resource, ResourceSubsystem subsystem) where T : IResource
    {
        var parent = GetSubsystem(subsystem);
        parent.RegisterChild(resource);
        return parent;
    }

    /// <inheritdoc />
    public IResource FindByPath(string path, string separator = "/")
    {
        if (string.IsNullOrEmpty(path))
        {
            return null;
        }

        var parts = path.Split([separator], StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0)
        {
            return null;
        }

        // Path must start with Root
        if (parts[0] != Root.Id)
        {
            return null;
        }

        // Navigate from root
        return Root.FindByPath(string.Join(separator, parts.Skip(1)), separator);
    }

    /// <summary>
    /// Disposes all resources in the tree (depth-first).
    /// </summary>
    public void Dispose()
    {
        Root.Dispose();
        GC.SuppressFinalize(this);
    }
}