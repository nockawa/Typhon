using System;
using System.Runtime.InteropServices;

namespace Typhon.Engine
{
    [StructLayout(LayoutKind.Sequential)]
    unsafe internal struct RootFileHeader
    {
        public fixed byte HeaderSignature[32];
        public int DatabaseFormatRevision;
        public ulong DatabaseFilesChunkSize;
        public fixed byte DatabaseName[64];
        public int OccupancyMapSPI;

        public string HeaderSignatureString
        {
            get
            {
                fixed (byte* s = HeaderSignature)
                {
                    return VirtualDiskManager.LoadString(s);
                }
            }
        }
        public string DatabaseNameString
        {
            get
            {
                fixed (byte* s = DatabaseName)
                {
                    return VirtualDiskManager.LoadString(s);
                }
            }
        }
    }

    [Flags]
    public enum PageBlockFlags : byte
    {
        None = 0,
        IsFree = 0x1,
        IsLogicalSegment = 0x2,
    }

    public enum PageBlockType : byte
    {
        None = 0,
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct PageBlockBaseHeader
    {
        /// <summary>
        /// Combination of one to many flags
        /// </summary>
        public PageBlockFlags Flags;
        /// <summary>
        /// Block Type
        /// </summary>
        public PageBlockType Type;
        /// <summary>
        /// 1 for single page block (8192 bytes), X for X x 8192 bytes to define a Combo Page Block (contiguous data on disk of X allocations)
        /// </summary>
        /// <remarks>
        /// Page Block can be variable in size, always a multiple of 8KiB. The combo defines the number of single Page Block to allocate linearly.
        /// The first Page Block has a 192 bytes header but the subsequent ones are plain raw data.
        /// </remarks>
        public short PageBlockComboSize;
        /// <summary>
        /// The Change Revision is incremented eEvery time the Page is written to disk.
        /// </summary>
        public int ChangeRevision;
        /// <summary>
        /// Revision number specific to the Page Block Type, to support basic versioning.
        /// </summary>
        public short FormatRevision;

        private short Padding0;

        /// <summary>
        /// If the Page Block is a Logical Segment, will store the index to the next block storing Map Data, 0 if there's none.
        /// </summary>
        public uint LogicalSegmentNextMapPBID;
        /// <summary>
        /// If the Page Block is a Logical Segment, will store the index to the next block storing Raw Data, 0 if there's none.
        /// </summary>
        public uint LogicalSegmentNextRawDataPBID;
    }
}
