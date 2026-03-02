using System.Collections.Generic;

namespace Typhon.Engine;

public readonly struct ViewDelta
{
    public readonly long[] Added;
    public readonly long[] Removed;
    public readonly long[] Modified;

    internal ViewDelta(HashSet<long> added, HashSet<long> removed, HashSet<long> modified)
    {
        Added = added.Count > 0 ? [.. added] : [];
        Removed = removed.Count > 0 ? [.. removed] : [];
        Modified = modified.Count > 0 ? [.. modified] : [];
    }

    public bool IsEmpty => Added.Length == 0 && Removed.Length == 0 && Modified.Length == 0;
}
