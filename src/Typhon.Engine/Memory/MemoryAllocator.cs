using JetBrains.Annotations;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;

namespace Typhon.Engine;

[PublicAPI]
public interface IMemoryAllocator
{
    MemoryBlockArray AllocateArray(string id, IResource parent, int size, bool zeroed = false);
    PinnedMemoryBlock AllocatePinned(string id, IResource parent, int size, bool zeroed = false, int alignment = 0);
}

[PublicAPI]
public class MemoryAllocatorOptions
{
    public string Name { get; set; } = "Default";
}

/// <summary>
/// Base class for service resources that auto-register under a subsystem.
/// Services are long-lived, typically singleton components in the engine.
/// </summary>
[PublicAPI]
public abstract class ServiceBase : IResource
{
    /// <summary>
    /// Creates a service and registers it under the specified subsystem.
    /// </summary>
    /// <param name="id">Unique identifier for this service.</param>
    /// <param name="owner">The resource registry to register with.</param>
    /// <param name="subsystem">Which subsystem to register under.</param>
    protected ServiceBase(string id, IResourceRegistry owner, ResourceSubsystem subsystem)
    {
        Id = id ?? throw new ArgumentNullException(nameof(id));
        Owner = owner ?? throw new ArgumentNullException(nameof(owner));
        Parent = Owner.Register(this, subsystem);
        CreatedAt = DateTime.UtcNow;
    }

    /// <inheritdoc />
    public abstract void Dispose();

    /// <inheritdoc />
    public string Id { get; }

    /// <inheritdoc />
    public ResourceType Type => ResourceType.Service;

    /// <inheritdoc />
    public IResource Parent { get; }

    /// <inheritdoc />
    public IEnumerable<IResource> Children => [];

    /// <inheritdoc />
    public DateTime CreatedAt { get; }

    /// <inheritdoc />
    public IResourceRegistry Owner { get; }

    /// <inheritdoc />
    public bool RegisterChild(IResource child) => false;

    /// <inheritdoc />
    public bool RemoveChild(IResource resource) => false;
}

[PublicAPI]
public class MemoryAllocator : ServiceBase, IMemoryAllocator
{
    private ConcurrentCollection<MemoryBlockBase> _blocks;

    public MemoryAllocator(IResourceRegistry resourceRegistry, MemoryAllocatorOptions options) :
        base(options?.Name ?? "DefaultMemoryAllocator", resourceRegistry, ResourceSubsystem.Allocation)
    {
        _blocks = new ConcurrentCollection<MemoryBlockBase>();
    }

    public override void Dispose()
    {
        
    }
    
    public MemoryBlockArray AllocateArray(string id, IResource parent, int size, bool zeroed = false)
    {
        if (size <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(size), "Size must be positive");
        }
        var block = zeroed ? GC.AllocateUninitializedArray<byte>(size) : GC.AllocateArray<byte>(size);

        var mb = new MemoryBlockArray(this, block, id, parent);
        _blocks.Add(mb);
        return mb;
    }
    
    public PinnedMemoryBlock AllocatePinned(string id, IResource parent, int size, bool zeroed = false, int alignment = 0)
    {
        if (size <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(size), "Size must be positive");
        }

        if (alignment < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(alignment), "Alignment cannot be negative");
        }

        if (!BitOperations.IsPow2(alignment))
        {
            throw new ArgumentException("Alignment must be a power of 2", nameof(alignment));
        }
        
        var unalignedSize = size + (alignment - 1);
        var block = zeroed ? GC.AllocateArray<byte>(unalignedSize, true) : GC.AllocateUninitializedArray<byte>(unalignedSize, true);
        
        var mb = new PinnedMemoryBlock(this, block, size, alignment, id, parent);
        _blocks.Add(mb);
        return mb;
    }
    
    internal void Remove(MemoryBlockBase block) => _blocks.Remove(block);
}