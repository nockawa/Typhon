using NUnit.Framework;
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace Typhon.Engine.Tests.Misc;

unsafe class BlockAllocatorTests
{
    [Test]
    public void AllocationWithResize()
    {
        const int pageSize = 4;
        const int allocationCount = 32;

        using var ba = new BlockAllocator(8, pageSize);

        var ids = stackalloc int[allocationCount];
        for (int i = 0; i < allocationCount; i++)
        {
            var span = ba.AllocateBlock(out var id).Cast<byte, long>();
            span[0] = i;

            ids[i] = id;
        }
            
        for (int i = 0; i < allocationCount; i++)
        {
            var span = ba.GetBlock(ids[i]).Cast<byte, long>();
            Assert.That(span[0], Is.EqualTo(i));
        }
    }

    [Test]
    public void AllocationAndFree()
    {
        const int pageSize = 4;
        const int allocationCount = 32;

        using var ba = new BlockAllocator(8, pageSize);

        for (int i = 0; i < allocationCount; i++)
        {
            ba.AllocateBlock(out var id1);
            ba.AllocateBlock(out var id2);
            ba.AllocateBlock(out var id3);
            ba.AllocateBlock(out var id4);

            ba.FreeBlock(id1);
            ba.FreeBlock(id2);
            ba.FreeBlock(id3);
            ba.FreeBlock(id4);
        }

        Assert.That(ba.Capacity, Is.EqualTo(pageSize));
        Assert.That(ba.AllocatedCount, Is.EqualTo(0));
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct TestStruct : ICleanable
    {
        public int A;
        public List<int> List;
        public int B;
        public void Cleanup()
        {
            //List = null;
        }
    }

    [Test]
    public void Test()
    {
        const int count = 1024;
        var sa = new StructAllocator<TestStruct>(count);


        for (int i = 0; i < count; i++)
        {
            ref var a = ref sa.Allocate(out var aid);
            a.A = 1;
            a.List = new List<int>(1024 * 1024 * 4);
            a.B = 2;

            sa.Free(aid);
        }

    }
}