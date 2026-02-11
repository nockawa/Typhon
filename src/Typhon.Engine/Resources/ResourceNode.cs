using JetBrains.Annotations;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;

namespace Typhon.Engine;

[DebuggerDisplay("{Id}, Children: {_children.Count})")]
[PublicAPI]
public class ResourceNode : IResource
{
    public string Id { get; }
    public ResourceType Type { get; }
    public IResource Parent { get; }
    public IEnumerable<IResource> Children => _children.Values;
    public DateTime CreatedAt { get; }
    public IResourceRegistry Owner { get; }

    /// <summary>
    /// Diagnostic metadata indicating how this resource responds when its capacity limit is reached.
    /// </summary>
    /// <remarks>
    /// <see cref="ExhaustionPolicy.None"/> for intermediate/structural nodes that don't own a bounded resource.
    /// </remarks>
    public ExhaustionPolicy ExhaustionPolicy { get; }

    public bool RegisterChild(IResource child) => _children.TryAdd(child.Id, child);
    public bool RemoveChild(IResource resource) => _children.TryRemove(resource.Id, out _);

    private ConcurrentDictionary<string, IResource> _children;

    public ResourceNode(string id, ResourceType type, IResource parent, ExhaustionPolicy exhaustionPolicy = ExhaustionPolicy.None)
    {
        Id = id ?? $"{GetType().Name}";
        Type = type;
        Parent = parent;
        Owner = parent.Owner;
        ExhaustionPolicy = exhaustionPolicy;
        CreatedAt = DateTime.UtcNow;
        Parent.RegisterChild(this);
        _children = new ConcurrentDictionary<string, IResource>();
    }

    internal static ResourceNode CreateRoot(ResourceRegistry registry) => new(registry);

    private ResourceNode(ResourceRegistry registry)
    {
        Id = "Root";
        Type = ResourceType.Node;
        Parent = null;
        Owner = registry;
        ExhaustionPolicy = ExhaustionPolicy.None;
        CreatedAt = DateTime.UtcNow;
        _children = new ConcurrentDictionary<string, IResource>();
    }

    public void Dispose()
    {
        Dispose(true);
        GC.SuppressFinalize(this);
    }

    protected virtual void Dispose(bool disposing)
    {
        if (_children == null)
        {
            return;
        }
        
        if (disposing)
        {
            foreach (var resource in _children.Values)
            {
                resource.Dispose();
            }
            _children.Clear();
            _children = null;
        }
    }
    
    ~ResourceNode()
    {
        Dispose(false);
    }
}