using JetBrains.Annotations;
using System;
using System.Runtime.InteropServices;

namespace Typhon.Engine;

[Flags]
public enum PageBlockFlags : byte
{
    None                 = 0x00,
    IsFree               = 0x01,
    IsLogicalSegment     = 0x02,
    IsLogicalSegmentRoot = 0x04
}

public enum PageBlockType : byte
{
    None = 0,
    OccupancyMap,
}

[PublicAPI]
[StructLayout(LayoutKind.Sequential, Pack = 4)]
public struct PageBaseHeader
{
    public static readonly int Offset = 0;
    unsafe public static readonly int Size = sizeof(PageBaseHeader);
    
    /// <summary>
    /// Combination of one to many flags
    /// </summary>
    public PageBlockFlags Flags;          // NOTE: keep this field as the first byte of the header because we perform direct access on it sometimes
    /// <summary>
    /// Block Type
    /// </summary>
    public PageBlockType Type;
    /// <summary>
    /// Revision number specific to the Page Block Type, to support basic versioning.
    /// </summary>
    public short FormatRevision;
    /// <summary>
    /// The Change Revision is incremented every time the Page is written to disk.
    /// </summary>
    public int ChangeRevision;
}