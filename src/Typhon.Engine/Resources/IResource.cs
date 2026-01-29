using JetBrains.Annotations;
using System;
using System.Collections.Generic;

namespace Typhon.Engine;

[PublicAPI]
public enum ResourceType
{
    None,
    Node,
    Service,
    Memory,
    File,
    Synchronization,
    Bitmap
}

[PublicAPI]
public interface IResource : IDisposable
{
    string Id { get; }
    ResourceType Type { get; }
    IResource Parent { get; }
    IEnumerable<IResource> Children { get; }
    DateTime CreatedAt { get; }
    IResourceRegistry Owner { get; }
    
    bool RegisterChild(IResource child);
    bool RemoveChild(IResource resource);
}