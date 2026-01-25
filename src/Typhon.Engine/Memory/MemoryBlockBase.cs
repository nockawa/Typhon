using JetBrains.Annotations;
using System;
using System.Collections.Generic;

namespace Typhon.Engine;

[PublicAPI]
public interface IMemoryResource : IResource
{
    int Size { get; }
}

[PublicAPI]
public abstract class MemoryBlockBase : IMemoryResource
{
    public MemoryAllocator Allocator { get; }
    public abstract int Size { get; }
    public abstract bool IsDisposed { get; }
    public abstract Span<byte> DataAsSpan { get; }

    public void Dispose()
    {
        Allocator.Remove(this);
        Parent.RemoveChild(this);
        GC.SuppressFinalize(this);
    }

    public string Id { get; }
    public ResourceType Type => ResourceType.Memory;
    public IResource Parent { get; }
    public abstract IEnumerable<IResource> Children { get; }
    public DateTime CreatedAt { get; }
    public IResourceRegistry Owner { get; }
    public bool RegisterChild(IResource child) => false;
    public bool RemoveChild(IResource resource) => false;

    protected MemoryBlockBase(MemoryAllocator allocator, string id, IResource parent)
    {
        Allocator = allocator ?? throw new ArgumentNullException(nameof(allocator), "Memory allocator cannot be null");
        Id = id;
        CreatedAt = DateTime.UtcNow;

        parent ??= TyphonServices.ResourceRegistry.Orphans;
        Parent = parent;
        Parent.RegisterChild(this);
        Owner = parent.Owner;
    }
}