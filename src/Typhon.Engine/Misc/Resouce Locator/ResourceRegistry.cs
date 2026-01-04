using JetBrains.Annotations;
using System;

namespace Typhon.Engine;

[PublicAPI]
public class ResourceRegistryOptions
{
    public string Name { get; set; }
}

[PublicAPI]
public class ResourceRegistry : IResourceRegistry
{
    public string Name { get; }
    
    public ResourceRegistry(ResourceRegistryOptions options)
    {
        Name = options?.Name ?? "DefaultResourceRegistry";
        Root = new ResourceNode("Root", ResourceType.Node, this);
        Services = new ResourceNode("Services", ResourceType.Node, Root);
        Orphans = new ResourceNode("Orphans", ResourceType.Node, Root);
    }
    
    public IResource Root { get; }
    public IResource Services { get; }
    public IResource Orphans { get; }

    public IResource RegisterService<T>(T service) where T : IResource
    {
        Services.RegisterChild(service);
        return Services;
    }

    public void Dispose()
    {
        Root.Dispose();
        GC.SuppressFinalize(this);
    }
}