using NUnit.Framework;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;

namespace Typhon.Engine.Tests.Misc
{
    unsafe class BlockAllocatorTests
    {
        [Test]
        public void AllocationWithResize()
        {
            const int pageSize = 4;
            const int allocationCount = 32;

            using var ba = new BlockAllocator(8, pageSize);

            var ids = new (IntPtr, int)[allocationCount];
            for (int i = 0; i < allocationCount; i++)
            {
                var addr = (long*)ba.Allocate(out var id);
                *addr = i;

                ids[i] = ((IntPtr)addr, id);
            }
            
            for (int i = 0; i < allocationCount; i++)
            {
                Assert.That(*(long*)ids[i].Item1, Is.EqualTo(i));
                Assert.That(*(long*)ba.GetAddress(ids[i].Item2), Is.EqualTo(i));
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
                ba.Allocate(out var id1);
                ba.Allocate(out var id2);
                ba.Allocate(out var id3);
                ba.Allocate(out var id4);

                ba.Free(id1);
                ba.Free(id2);
                ba.Free(id3);
                ba.Free(id4);
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
}
