using JetBrains.Annotations;
using System.Runtime.InteropServices;

namespace Typhon.Schema.Definition;

[PublicAPI]
[StructLayout(LayoutKind.Sequential)]
public struct ComponentCollection<T> where T : unmanaged
{
    internal int _bufferId;
}
