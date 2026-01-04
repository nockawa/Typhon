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

public abstract class ServiceBase : IResource
{
    protected ServiceBase(string id, IResourceRegistry owner)
    {
        Id = id;
        Owner = owner;
        Parent = Owner.RegisterService(this);
        CreatedAt = DateTime.UtcNow;
    }
    public abstract void Dispose();

    public string Id { get; }
    public ResourceType Type => ResourceType.Service;
    public IResource Parent { get; }
    public IEnumerable<IResource> Children => [];
    public DateTime CreatedAt { get; }
    public IResourceRegistry Owner { get; }
    public bool RegisterChild(IResource child) => throw new NotImplementedException();
    public bool RemoveChild(IResource resource) => throw new NotImplementedException();
}

[PublicAPI]
public class MemoryAllocator : ServiceBase, IMemoryAllocator
{
    private ConcurrentCollection<MemoryBlockBase> _blocks;

    public MemoryAllocator(IResourceRegistry resourceRegistry, MemoryAllocatorOptions options) : 
        base(options?.Name ?? "DefaultMemoryAllocator", resourceRegistry)
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