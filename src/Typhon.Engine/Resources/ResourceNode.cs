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
        : this(id, type, parent.Owner, exhaustionPolicy)
    {
        Parent = parent;
    }

    internal ResourceNode(string id, ResourceType type, IResourceRegistry owner, ExhaustionPolicy exhaustionPolicy = ExhaustionPolicy.None)
    {
        Id = id;
        Type = type;
        Parent = null;
        Owner = owner;
        ExhaustionPolicy = exhaustionPolicy;
        CreatedAt = DateTime.UtcNow;
        _children = new ConcurrentDictionary<string, IResource>();
    }

    public void Dispose()
    {
        foreach (var resource in _children.Values)
        {
            resource.Dispose();
        }
        _children.Clear();
        GC.SuppressFinalize(this);
    }
}