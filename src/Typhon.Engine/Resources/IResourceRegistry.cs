using JetBrains.Annotations;
using System;

namespace Typhon.Engine;

[PublicAPI]
public interface IResourceRegistry : IDisposable
{
    IResource Root { get; }
    IResource Services { get; }
    IResource Orphans { get; }
    IResource RegisterService<T>(T service) where T : IResource;
}