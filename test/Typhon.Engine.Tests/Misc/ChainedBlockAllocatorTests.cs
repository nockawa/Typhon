using NUnit.Framework;
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace Typhon.Engine.Tests.Misc;

[TestFixture]
public class ChainedBlockAllocatorTests
{
    #region Constructor and Properties Tests

    [Test]
    public void Constructor_ValidParameters_CreatesAllocator()
    {
        // Arrange & Act
        using var allocator = new ChainedBlockAllocator(16, 64);

        // Assert
        Assert.That(allocator.Stride, Is.EqualTo(16));
        Assert.That(allocator.Capacity, Is.EqualTo(64));
        // AllocatedCount is 1 because block 0 is reserved as sentinel
        Assert.That(allocator.AllocatedCount, Is.EqualTo(1));
    }

    [Test]
    public void Stride_ReturnsUserStride_ExcludingChainHeader()
    {
        // The internal stride is stride+4 for the chain header
        // But the public Stride property should return the user-specified stride
        using var allocator = new ChainedBlockAllocator(32, 64);

        Assert.That(allocator.Stride, Is.EqualTo(32));
    }

    [TestCase(8)]
    [TestCase(16)]
    [TestCase(64)]
    [TestCase(128)]
    public void Constructor_DifferentStrides_WorksCorrectly(int stride)
    {
        using var allocator = new ChainedBlockAllocator(stride, 64);
        Assert.That(allocator.Stride, Is.EqualTo(stride));
    }

    #endregion

    #region Allocate Tests

    [Test]
    public void Allocate_SingleBlock_ReturnsValidPointerAndId()
    {
        using var allocator = new ChainedBlockAllocator(16, 64);

        allocator.AllocateBlock(out var blockId, true);

        Assert.That(blockId, Is.GreaterThan(0)); // Block 0 is reserved
        Assert.That(allocator.AllocatedCount, Is.EqualTo(2)); // 1 reserved + 1 allocated
    }

    [Test]
    public void Allocate_MultipleBlocks_ReturnsUniqueIds()
    {
        using var allocator = new ChainedBlockAllocator(16, 64);
        var ids = new HashSet<int>();

        for (int i = 0; i < 32; i++)
        {
            allocator.AllocateBlock(out var blockId, true);
            Assert.That(ids.Add(blockId), Is.True, $"Block ID {blockId} was already allocated");
        }

        Assert.That(allocator.AllocatedCount, Is.EqualTo(33)); // 1 reserved + 32 allocated
    }

    [Test]
    public void Allocate_DataIntegrity_PreservesWrittenData()
    {
        using var allocator = new ChainedBlockAllocator(sizeof(long), 64);
        var allocations = new (StoreSpan ptr, int id, long value)[50];

        // Allocate multiple blocks and write unique values
        for (int i = 0; i < 50; i++)
        {
            var ptr = allocator.AllocateBlock(out var blockId, true).Cast<byte, long>();
            var value = (long)(i * 12345 + 67890);
            ptr[0] = value;
            allocations[i] = (ptr.ToStoreSpan(), blockId, value);
        }

        // Verify all values are intact
        for (int i = 0; i < allocations.Length; i++)
        {
            var span = allocations[i].ptr.ToSpan<long>();
            var expectedValue = allocations[i].value;
            var actualValue = span[0];
            Assert.That(actualValue, Is.EqualTo(expectedValue), $"Data corruption at block {allocations[i].id}");
        }
    }

    [Test]
    public void Allocate_TriggerResize_ExpandsCapacity()
    {
        const int initialPageSize = 4;
        using var allocator = new ChainedBlockAllocator(16, initialPageSize);

        Assert.That(allocator.Capacity, Is.EqualTo(initialPageSize));

        // Allocate more than the initial capacity
        for (int i = 0; i < initialPageSize + 1; i++)
        {
            allocator.AllocateBlock(out _, true);
        }

        Assert.That(allocator.Capacity, Is.GreaterThan(initialPageSize));
        Assert.That(allocator.AllocatedCount, Is.EqualTo(initialPageSize + 2)); // 1 reserved + (initialPageSize + 1) allocated
    }

    [Test]
    public void Allocate_LargeAllocationCount_MaintainsIntegrity()
    {
        const int pageSize = 16;
        const int allocationCount = 256;

        using var allocator = new ChainedBlockAllocator(sizeof(int), pageSize);
        var allocations = new (int id, int value)[allocationCount];

        for (int i = 0; i < allocationCount; i++)
        {
            var span = allocator.AllocateBlock(out var blockId, true).Cast<byte, int>();
            span[0] = i;
            allocations[i] = (blockId, i);
        }

        // Verify all data is intact
        for (int i = 0; i < allocationCount; i++)
        {
            var span = allocator.GetBlockData(allocations[i].id).Cast<byte, int>();
            Assert.That(span[0], Is.EqualTo(allocations[i].value));
        }
    }

    #endregion

    #region GetAddress Tests

    [Test]
    public void GetAddress_ZeroBlockId_ReturnsNull()
    {
        using var allocator = new ChainedBlockAllocator(16, 64);

        var ptr = allocator.GetBlockData(0);

        Assert.That(ptr.IsEmpty, Is.True);
    }

    [Test]
    public void GetAddress_ValidBlockId_ReturnsSameAsAllocate()
    {
        using var allocator = new ChainedBlockAllocator(16, 64);

        allocator.AllocateBlock(out var blockId, true);
        var allocatedAddr = allocator.AsIntPtr(blockId);
        var retrievedAddr = allocator.AsIntPtr(blockId);

        Assert.That(retrievedAddr, Is.EqualTo(allocatedAddr));
    }

    [Test]
    public void GetAddress_MultipleBlocks_ReturnsCorrectAddresses()
    {
        using var allocator = new ChainedBlockAllocator(sizeof(long), 64);
        var blocks = new (IntPtr ptr, int id)[20];

        for (int i = 0; i < 20; i++)
        {
            var span = allocator.AllocateBlock(out var id, true);
            span.Cast<byte, long>()[0] = i * 1000;
            blocks[i] = (allocator.AsIntPtr(id), id);
        }

        for (int i = 0; i < blocks.Length; i++)
        {
            var originalPtr = blocks[i].ptr;
            var retrievedPtr = allocator.AsIntPtr(blocks[i].id);
            Assert.That(retrievedPtr, Is.EqualTo(originalPtr));
        }
    }

    #endregion

    #region Chain Tests

    [Test]
    public void Chain_TwoBlocks_CreatesValidChain()
    {
        using var allocator = new ChainedBlockAllocator(sizeof(int), 64);

        var ptrA = allocator.AllocateBlock(out var blockA, true);
        var ptrB = allocator.AllocateBlock(out var blockB, false);

        ptrA.Cast<byte, int>()[0] = 100;
        ptrB.Cast<byte, int>()[0] = 200;

        allocator.Chain(blockA, blockB);

        // Verify chain: A -> B
        var nextPtr = allocator.NextBlock(blockA, out _);
        Assert.That(nextPtr.IsEmpty, Is.False);
        Assert.That(MemoryMarshal.Read<int>(nextPtr), Is.EqualTo(200)); // NextBlock returns data directly
    }

    [Test]
    public void Chain_ThreeBlocksSequentially_CreatesLongChain()
    {
        using var allocator = new ChainedBlockAllocator(sizeof(int), 64);

        var ptrA = allocator.AllocateBlock(out var blockA, true);
        var ptrB = allocator.AllocateBlock(out var blockB, false);
        var ptrC = allocator.AllocateBlock(out var blockC, false);

        ptrA.Cast<byte, int>()[0] = 1;
        ptrB.Cast<byte, int>()[0] = 2;
        ptrC.Cast<byte, int>()[0] = 3;

        // Chain A -> B, then B -> C
        allocator.Chain(blockA, blockB);
        allocator.Chain(blockB, blockC);

        // Verify chain A -> B -> C using enumerator
        var values = new List<int>();
        foreach (var blockId in allocator.EnumerateChainedBlock(blockA))
        {
            var block = allocator.GetBlockData(blockId);
            values.Add(MemoryMarshal.Read<int>(block));
        }

        Assert.That(values, Is.EqualTo([1, 2, 3]));
    }

    [Test]
    public void Chain_InsertBlockIntoExistingChain_InsertsCorrectly()
    {
        using var allocator = new ChainedBlockAllocator(sizeof(int), 64);

        // Create chain A -> B -> C
        var ptrA = allocator.AllocateBlock(out var blockA, true);
        var ptrB = allocator.AllocateBlock(out var blockB, false);
        var ptrC = allocator.AllocateBlock(out var blockC, false);
        var ptrD = allocator.AllocateBlock(out var blockD, false);

        ptrA.Cast<byte, int>()[0] = 1;
        ptrB.Cast<byte, int>()[0] = 2;
        ptrC.Cast<byte, int>()[0] = 3;
        ptrD.Cast<byte, int>()[0] = 4;

        // Build A -> B -> C
        allocator.Chain(blockA, blockB);
        allocator.Chain(blockB, blockC);

        // Now chain D after A (should result in A -> D -> B -> C per the Chain logic)
        // According to the Chain method: if A already chains to B,C and we chain D after A,
        // D will be inserted and the old chain (B,C) will be appended to end of D's chain
        allocator.Chain(blockA, blockD);

        // Expected: A -> D -> B -> C
        var values = new List<int>();
        foreach (var blockId in allocator.EnumerateChainedBlock(blockA))
        {
            var block = allocator.GetBlockData(blockId);
            values.Add(MemoryMarshal.Read<int>(block));
        }

        Assert.That(values, Is.EqualTo([1, 4, 2, 3]));
    }

    [Test]
    public void Chain_ChainWithZero_BreaksChain()
    {
        using var allocator = new ChainedBlockAllocator(sizeof(int), 64);

        var ptrA = allocator.AllocateBlock(out var blockA, true);
        allocator.AllocateBlock(out var blockB, false);

        allocator.Chain(blockA, blockB);

        // Verify chain exists
        Assert.That(allocator.NextBlock(blockA, out _).IsEmpty, Is.False);

        // Break the chain
        allocator.Chain(blockA, 0);

        // Verify chain is broken
        Assert.That(allocator.NextBlock(blockA, out _).IsEmpty, Is.True);
    }

    [Test]
    public void Chain_MergeTwoChains_CreatesCorrectOrder()
    {
        using var allocator = new ChainedBlockAllocator(sizeof(int), 64);

        // Create chain 1: A -> B -> C
        var ptrA = allocator.AllocateBlock(out var blockA, true);
        var ptrB = allocator.AllocateBlock(out var blockB, false);
        var ptrC = allocator.AllocateBlock(out var blockC, false);

        // Create chain 2: D -> E -> F
        var ptrD = allocator.AllocateBlock(out var blockD, true);
        var ptrE = allocator.AllocateBlock(out var blockE, false);
        var ptrF = allocator.AllocateBlock(out var blockF, false);

        ptrA.Cast<byte, int>()[0] = 1;
        ptrB.Cast<byte, int>()[0] = 2;
        ptrC.Cast<byte, int>()[0] = 3;
        ptrD.Cast<byte, int>()[0] = 4;
        ptrE.Cast<byte, int>()[0] = 5;
        ptrF.Cast<byte, int>()[0] = 6;

        allocator.Chain(blockA, blockB);
        allocator.Chain(blockB, blockC);
        allocator.Chain(blockD, blockE);
        allocator.Chain(blockE, blockF);

        // Merge: chain D after A
        // Expected per Chain logic: A -> D -> E -> F -> B -> C
        allocator.Chain(blockA, blockD);

        var values = new List<int>();
        foreach (var blockId in allocator.EnumerateChainedBlock(blockA))
        {
            var block = allocator.GetBlockData(blockId);
            values.Add(MemoryMarshal.Read<int>(block));
        }

        Assert.That(values, Is.EqualTo([1, 4, 5, 6, 2, 3]));
    }

    [Test]
    public void Chain_DataIntegrity_PreservesAllBlockData()
    {
        using var allocator = new ChainedBlockAllocator(sizeof(long), 64);
        const int blockCount = 10;
        var blocks = new (int id, long value)[blockCount];

        // Allocate and initialize blocks
        for (int i = 0; i < blockCount; i++)
        {
            var span = allocator.AllocateBlock(out var id, i == 0);
            var value = (long)(i * 11111);
            span.Cast<byte, long>()[0] = value;
            blocks[i] = (id, value);
        }

        // Chain all blocks: 0 -> 1 -> 2 -> ... -> 9
        for (int i = 0; i < blockCount - 1; i++)
        {
            allocator.Chain(blocks[i].id, blocks[i + 1].id);
        }

        // Verify all data is intact
        for (int i = 0; i < blocks.Length; i++)
        {
            var span = allocator.GetBlockData(blocks[i].id);
            Assert.That(MemoryMarshal.Read<long>(span), Is.EqualTo(blocks[i].value));
        }
    }

    #endregion

    #region RemoveNextBlock Tests

    [Test]
    public void RemoveNextBlock_ZeroBlockId_ReturnsZero()
    {
        using var allocator = new ChainedBlockAllocator(16, 64);

        var result = allocator.RemoveNextBlock(0);

        Assert.That(result, Is.EqualTo(0));
    }

    [Test]
    public void RemoveNextBlock_NoNextBlock_ReturnsZero()
    {
        using var allocator = new ChainedBlockAllocator(16, 64);

        allocator.AllocateBlock(out var blockA, true);

        var result = allocator.RemoveNextBlock(blockA);

        Assert.That(result, Is.EqualTo(0));
    }

    [Test]
    public void RemoveNextBlock_HasNextBlock_ReturnsAndRemovesIt()
    {
        using var allocator = new ChainedBlockAllocator(sizeof(int), 64);

        var ptrA = allocator.AllocateBlock(out var blockA, true);
        var ptrB = allocator.AllocateBlock(out var blockB, false);

        ptrA.Cast<byte, int>()[0] = 100;
        ptrB.Cast<byte, int>()[0] = 200;

        allocator.Chain(blockA, blockB);

        var removedId = allocator.RemoveNextBlock(blockA);

        Assert.That(removedId, Is.EqualTo(blockB));
        // A should now have no next block
        Assert.That(allocator.NextBlock(blockA, out _).IsEmpty, Is.True);
    }

    [Test]
    public void RemoveNextBlock_ThreeBlockChain_RemovesMiddle()
    {
        using var allocator = new ChainedBlockAllocator(sizeof(int), 64);

        var ptrA = allocator.AllocateBlock(out var blockA, true);
        var ptrB = allocator.AllocateBlock(out var blockB, false);
        var ptrC = allocator.AllocateBlock(out var blockC, false);

        ptrA.Cast<byte, int>()[0] = 1;
        ptrB.Cast<byte, int>()[0] = 2;
        ptrC.Cast<byte, int>()[0] = 3;

        allocator.Chain(blockA, blockB);
        allocator.Chain(blockB, blockC);

        // Chain: A -> B -> C
        // Remove B
        var removedId = allocator.RemoveNextBlock(blockA);

        Assert.That(removedId, Is.EqualTo(blockB));

        // Chain should now be A -> C
        var values = new List<int>();
        foreach (var blockId in allocator.EnumerateChainedBlock(blockA))
        {
            var block = allocator.GetBlockData(blockId);
            values.Add(MemoryMarshal.Read<int>(block));
        }

        Assert.That(values, Is.EqualTo([1, 3]));
    }

    [Test]
    public void RemoveNextBlock_RemovedBlockIsUnchained()
    {
        using var allocator = new ChainedBlockAllocator(sizeof(int), 64);

        allocator.AllocateBlock(out var blockA, true);
        allocator.AllocateBlock(out var blockB, false);
        allocator.AllocateBlock(out var blockC, false);

        allocator.Chain(blockA, blockB);
        allocator.Chain(blockB, blockC);

        // Remove B from chain
        var removedId = allocator.RemoveNextBlock(blockA);

        // Verify B is now unchained (its next pointer should be 0)
        Assert.That(allocator.NextBlock(removedId, out _).IsEmpty, Is.True);
    }

    [Test]
    public void RemoveNextBlock_DataIntegrity_PreservesAllData()
    {
        using var allocator = new ChainedBlockAllocator(sizeof(long), 64);

        var ptrA = allocator.AllocateBlock(out var blockA, true);
        var ptrB = allocator.AllocateBlock(out var blockB, false);
        var ptrC = allocator.AllocateBlock(out var blockC, false);

        ptrA.Cast<byte, long>()[0] = 111;
        ptrB.Cast<byte, long>()[0] = 222;
        ptrC.Cast<byte, long>()[0] = 333;

        allocator.Chain(blockA, blockB);
        allocator.Chain(blockB, blockC);

        allocator.RemoveNextBlock(blockA);

        // All data should still be intact
        Assert.That(MemoryMarshal.Read<long>(allocator.GetBlockData(blockA)), Is.EqualTo(111));
        Assert.That(MemoryMarshal.Read<long>(allocator.GetBlockData(blockB)), Is.EqualTo(222));
        Assert.That(MemoryMarshal.Read<long>(allocator.GetBlockData(blockC)), Is.EqualTo(333));
    }

    #endregion

    #region NextBlock Tests

    [Test]
    public void NextBlock_UnchainedBlock_ReturnsNull()
    {
        using var allocator = new ChainedBlockAllocator(16, 64);

        allocator.AllocateBlock(out var blockId, true);

        var next = allocator.NextBlock(blockId, out _);

        Assert.That(next.IsEmpty, Is.True);
    }

    [Test]
    public void NextBlock_ChainedBlock_ReturnsNextBlockAddress()
    {
        using var allocator = new ChainedBlockAllocator(sizeof(int), 64);

        var ptrA = allocator.AllocateBlock(out var blockA, true);
        allocator.AllocateBlock(out var blockB, false);

        allocator.Chain(blockA, blockB);

        var next = allocator.NextBlock(blockA, out _);

        Assert.That(next.IsEmpty, Is.False);
        // NextBlock now returns data directly (no header skip needed)
    }

    [Test]
    public void NextBlock_NavigateEntireChain_Works()
    {
        using var allocator = new ChainedBlockAllocator(sizeof(int), 64);

        var ptrA = allocator.AllocateBlock(out var blockA, true);
        var ptrB = allocator.AllocateBlock(out var blockB, false);
        var ptrC = allocator.AllocateBlock(out var blockC, false);
        var ptrD = allocator.AllocateBlock(out var blockD, false);

        ptrA.Cast<byte, int>()[0] = 1;
        ptrB.Cast<byte, int>()[0] = 2;
        ptrC.Cast<byte, int>()[0] = 3;
        ptrD.Cast<byte, int>()[0] = 4;

        allocator.Chain(blockA, blockB);
        allocator.Chain(blockB, blockC);
        allocator.Chain(blockC, blockD);

        // Navigate using NextBlock - now returns data directly
        var values = new List<int>();
        var current = blockA;
        while (current != 0)
        {
            values.Add(allocator.GetBlockData(current).Cast<byte, int>()[0]);
            allocator.NextBlock(current, out current);
        }

        Assert.That(values, Is.EqualTo([1, 2, 3, 4]));
    }

    #endregion

    #region Free Tests

    [Test]
    public void Free_SingleBlock_DecreasesAllocatedCount()
    {
        using var allocator = new ChainedBlockAllocator(16, 64);

        allocator.AllocateBlock(out var blockId, true);
        Assert.That(allocator.AllocatedCount, Is.EqualTo(2)); // 1 reserved + 1 allocated

        allocator.Free(blockId);
        Assert.That(allocator.AllocatedCount, Is.EqualTo(1)); // Only reserved block remains
    }

    [Test]
    public void Free_ChainedBlocks_FreesAllInChain()
    {
        using var allocator = new ChainedBlockAllocator(16, 64);

        allocator.AllocateBlock(out var blockA, true);
        allocator.AllocateBlock(out var blockB, false);
        allocator.AllocateBlock(out var blockC, false);

        allocator.Chain(blockA, blockB);
        allocator.Chain(blockB, blockC);

        Assert.That(allocator.AllocatedCount, Is.EqualTo(4)); // 1 reserved + 3 allocated

        allocator.Free(blockA);

        Assert.That(allocator.AllocatedCount, Is.EqualTo(1)); // Only reserved block remains
    }

    [Test]
    public void Free_PartialChain_FreesOnlyFromStartBlock()
    {
        using var allocator = new ChainedBlockAllocator(16, 64);

        allocator.AllocateBlock(out var blockA, true);
        allocator.AllocateBlock(out var blockB, false);
        allocator.AllocateBlock(out var blockC, false);
        allocator.AllocateBlock(out var blockD, false);

        allocator.Chain(blockA, blockB);
        allocator.Chain(blockB, blockC);
        allocator.Chain(blockC, blockD);

        Assert.That(allocator.AllocatedCount, Is.EqualTo(5)); // 1 reserved + 4 allocated

        // Free starting from B (should free B, C, D)
        allocator.Free(blockB);

        Assert.That(allocator.AllocatedCount, Is.EqualTo(2)); // 1 reserved + A remains
    }

    [Test]
    public void Free_MultipleChains_FreesCorrectly()
    {
        using var allocator = new ChainedBlockAllocator(16, 64);

        // Chain 1: A -> B
        allocator.AllocateBlock(out var blockA, true);
        allocator.AllocateBlock(out var blockB, false);
        allocator.Chain(blockA, blockB);

        // Chain 2: C -> D
        allocator.AllocateBlock(out var blockC, true);
        allocator.AllocateBlock(out var blockD, false);
        allocator.Chain(blockC, blockD);

        Assert.That(allocator.AllocatedCount, Is.EqualTo(5)); // 1 reserved + 4 allocated

        allocator.Free(blockA);
        Assert.That(allocator.AllocatedCount, Is.EqualTo(3)); // 1 reserved + 2 remaining

        allocator.Free(blockC);
        Assert.That(allocator.AllocatedCount, Is.EqualTo(1)); // Only reserved block remains
    }

    [Test]
    public void Free_ThenReallocate_Works()
    {
        const int pageSize = 4;
        using var allocator = new ChainedBlockAllocator(sizeof(int), pageSize);

        // Fill up initial page
        var ids = new int[pageSize];
        for (int i = 0; i < pageSize; i++)
        {
            var span = allocator.AllocateBlock(out ids[i], true);
            span.Cast<byte, int>()[0] = i;
        }

        Assert.That(allocator.AllocatedCount, Is.EqualTo(pageSize + 1)); // 1 reserved + pageSize allocated

        // Free all
        for (int i = 0; i < pageSize; i++)
        {
            allocator.Free(ids[i]);
        }

        Assert.That(allocator.AllocatedCount, Is.EqualTo(1)); // Only reserved block remains

        // Reallocate and verify
        for (int i = 0; i < pageSize; i++)
        {
            var span = allocator.AllocateBlock(out int _, true);
            span.Cast<byte, int>()[0] = i + 100;
        }

        Assert.That(allocator.AllocatedCount, Is.EqualTo(pageSize + 1)); // 1 reserved + pageSize allocated
    }

    #endregion

    #region Enumerator Tests

    [Test]
    public void Enumerator_EmptyChain_NoIterations()
    {
        using var allocator = new ChainedBlockAllocator(16, 64);

        var count = 0;
        foreach (var _ in allocator.EnumerateChainedBlock(0))
        {
            count++;
        }

        Assert.That(count, Is.EqualTo(0));
    }

    [Test]
    public void Enumerator_SingleBlock_OneIteration()
    {
        using var allocator = new ChainedBlockAllocator(sizeof(int), 64);

        var span = allocator.AllocateBlock(out var blockId, true);
        span.Cast<byte, int>()[0] = 42;

        var count = 0;
        var values = new List<int>();
        var enumerator = allocator.EnumerateChainedBlock(blockId);
        foreach (var curBlockId in enumerator)
        {
            count++;
            var block = allocator.GetBlockData(curBlockId);
            values.Add(MemoryMarshal.Read<int>(block));
        }

        Assert.That(count, Is.EqualTo(1));
        Assert.That(values, Is.EqualTo([42]));
    }

    [Test]
    public void Enumerator_ChainedBlocks_IteratesAll()
    {
        using var allocator = new ChainedBlockAllocator(sizeof(int), 64);

        var ptrA = allocator.AllocateBlock(out var blockA, true);
        var ptrB = allocator.AllocateBlock(out var blockB, false);
        var ptrC = allocator.AllocateBlock(out var blockC, false);

        ptrA.Cast<byte, int>()[0] = 10;
        ptrB.Cast<byte, int>()[0] = 20;
        ptrC.Cast<byte, int>()[0] = 30;

        allocator.Chain(blockA, blockB);
        allocator.Chain(blockB, blockC);

        var values = new List<int>();
        foreach (var blockId in allocator.EnumerateChainedBlock(blockA))
        {
            var block = allocator.GetBlockData(blockId);
            values.Add(MemoryMarshal.Read<int>(block));
        }

        Assert.That(values, Is.EqualTo([10, 20, 30]));
    }

    [Test]
    public void Enumerator_CurrentBlockId_ReturnsCorrectIds()
    {
        using var allocator = new ChainedBlockAllocator(sizeof(int), 64);

        allocator.AllocateBlock(out var blockA, true);
        allocator.AllocateBlock(out var blockB, false);
        allocator.AllocateBlock(out var blockC, false);

        allocator.Chain(blockA, blockB);
        allocator.Chain(blockB, blockC);

        var blockIds = new List<int>();
        foreach (var blockId in allocator.EnumerateChainedBlock(blockA))
        {
            blockIds.Add(blockId);
        }

        Assert.That(blockIds, Is.EqualTo([blockA, blockB, blockC]));
    }

    [Test]
    public void Enumerator_LongChain_IteratesAll()
    {
        using var allocator = new ChainedBlockAllocator(sizeof(int), 64);
        const int chainLength = 50;

        var blocks = new int[chainLength];
        for (int i = 0; i < chainLength; i++)
        {
            var span = allocator.AllocateBlock(out blocks[i], i == 0);
            span.Cast<byte, int>()[0] = i;
        }

        for (int i = 0; i < chainLength - 1; i++)
        {
            allocator.Chain(blocks[i], blocks[i + 1]);
        }

        var values = new List<int>();
        foreach (var blockId in allocator.EnumerateChainedBlock(blocks[0]))
        {
            var block = allocator.GetBlockData(blockId);
            values.Add(MemoryMarshal.Read<int>(block));
        }

        Assert.That(values.Count, Is.EqualTo(chainLength));
        for (int i = 0; i < chainLength; i++)
        {
            Assert.That(values[i], Is.EqualTo(i));
        }
    }

    [Test]
    public void Enumerator_StartFromMiddleOfChain_IteratesFromThere()
    {
        using var allocator = new ChainedBlockAllocator(sizeof(int), 64);

        var ptrA = allocator.AllocateBlock(out var blockA, true);
        var ptrB = allocator.AllocateBlock(out var blockB, false);
        var ptrC = allocator.AllocateBlock(out var blockC, false);

        ptrA.Cast<byte, int>()[0] = 1;
        ptrB.Cast<byte, int>()[0] = 2;
        ptrC.Cast<byte, int>()[0] = 3;

        allocator.Chain(blockA, blockB);
        allocator.Chain(blockB, blockC);

        // Start enumeration from blockB
        var values = new List<int>();
        foreach (var blockId in allocator.EnumerateChainedBlock(blockB))
        {
            var block = allocator.GetBlockData(blockId);
            values.Add(MemoryMarshal.Read<int>(block));
        }

        Assert.That(values, Is.EqualTo([2, 3]));
    }

    #endregion

    #region Edge Cases and Stress Tests

    [Test]
    public void Dispose_AfterOperations_WorksCorrectly()
    {
        var allocator = new ChainedBlockAllocator(16, 64);

        allocator.AllocateBlock(out var blockA, true);
        allocator.AllocateBlock(out var blockB, false);
        allocator.Chain(blockA, blockB);

        allocator.Dispose();

        // Should not throw on double dispose
        Assert.DoesNotThrow(() => allocator.Dispose());
    }

    [Test]
    public void LargeStride_WorksCorrectly()
    {
        const int largeStride = 1024;
        using var allocator = new ChainedBlockAllocator(largeStride, 16);

        var ptr = allocator.AllocateBlock(out var blockId, true);

        // Fill the entire block
        for (int i = 0; i < largeStride; i++)
        {
            ptr[i] = (byte)(i % 256);
        }

        // Verify
        var retrieved = allocator.GetBlockData(blockId);
        for (int i = 0; i < largeStride; i++)
        {
            Assert.That(retrieved[i], Is.EqualTo((byte)(i % 256)));
        }
    }

    [Test]
    public void SmallStride_WorksCorrectly()
    {
        const int smallStride = 1;
        using var allocator = new ChainedBlockAllocator(smallStride, 64);

        var span = allocator.AllocateBlock(out var blockId, true);
        span[0] = 42;

        Assert.That(allocator.GetBlockData(blockId)[0], Is.EqualTo(42));
        Assert.That(allocator.Stride, Is.EqualTo(smallStride));
    }

    [Test]
    public void InterleavedAllocateAndFree_MaintainsIntegrity()
    {
        // Small page size (4) triggers multiple _blockMap.Resize calls - edge case bug
        using var allocator = new ChainedBlockAllocator(sizeof(int), 4);
        var activeBlocks = new Dictionary<int, int>();

        for (int iteration = 0; iteration < 50; iteration++)
        {
            // Allocate some
            for (int i = 0; i < 5; i++)
            {
                var span = allocator.AllocateBlock(out var id, true);
                var value = iteration * 1000 + i;
                span.Cast<byte, int>()[0] = value;
                activeBlocks[id] = value;
            }

            // Free some (roughly half)
            var toRemove = new List<int>();
            foreach (var kvp in activeBlocks)
            {
                if (kvp.Key % 2 == 0)
                {
                    allocator.Free(kvp.Key);
                    toRemove.Add(kvp.Key);
                }
            }

            foreach (var id in toRemove)
            {
                activeBlocks.Remove(id);
            }

            // Verify remaining blocks
            foreach (var kvp in activeBlocks)
            {
                var span = allocator.GetBlockData(kvp.Key);
                Assert.That(MemoryMarshal.Read<int>(span), Is.EqualTo(kvp.Value));
            }
        }
    }

    [Test]
    public void ChainOperations_AfterResize_WorkCorrectly()
    {
        const int pageSize = 4;
        using var allocator = new ChainedBlockAllocator(sizeof(int), pageSize);

        // Allocate beyond initial capacity to trigger resize
        var blocks = new List<int>();
        for (int i = 0; i < pageSize * 3; i++)
        {
            var span = allocator.AllocateBlock(out var id, i == 0);
            span.Cast<byte, int>()[0] = i;
            blocks.Add(id);
        }

        // Chain blocks across different pages
        for (int i = 0; i < blocks.Count - 1; i++)
        {
            allocator.Chain(blocks[i], blocks[i + 1]);
        }

        // Verify chain integrity
        var values = new List<int>();
        foreach (var blockId in allocator.EnumerateChainedBlock(blocks[0]))
        {
            var block = allocator.GetBlockData(blockId);
            values.Add(MemoryMarshal.Read<int>(block));
        }

        Assert.That(values.Count, Is.EqualTo(blocks.Count));
        for (int i = 0; i < blocks.Count; i++)
        {
            Assert.That(values[i], Is.EqualTo(i));
        }
    }

    [Test]
    public void ComplexChainManipulation_MaintainsIntegrity()
    {
        using var allocator = new ChainedBlockAllocator(sizeof(int), 64);

        // Allocate 6 blocks
        var blocks = new int[6];
        for (int i = 0; i < 6; i++)
        {
            var span = allocator.AllocateBlock(out blocks[i], i is 0 or 3);
            span.Cast<byte, int>()[0] = i + 1; // 1, 2, 3, 4, 5, 6
        }

        // Create chain: 1 -> 2 -> 3
        allocator.Chain(blocks[0], blocks[1]);
        allocator.Chain(blocks[1], blocks[2]);

        // Create chain: 4 -> 5 -> 6
        allocator.Chain(blocks[3], blocks[4]);
        allocator.Chain(blocks[4], blocks[5]);

        // Merge chains: 1 -> 4 -> 5 -> 6 -> 2 -> 3
        allocator.Chain(blocks[0], blocks[3]);

        var values = new List<int>();
        foreach (var blockId in allocator.EnumerateChainedBlock(blocks[0]))
        {
            var block = allocator.GetBlockData(blockId);
            values.Add(MemoryMarshal.Read<int>(block));
        }

        Assert.That(values, Is.EqualTo([1, 4, 5, 6, 2, 3]));

        // Remove block 5 from chain (which is after block 4)
        allocator.RemoveNextBlock(blocks[3]); // Removes 5

        values.Clear();
        foreach (var blockId in allocator.EnumerateChainedBlock(blocks[0]))
        {
            var block = allocator.GetBlockData(blockId);
            values.Add(MemoryMarshal.Read<int>(block));
        }

        // Expected: 1 -> 4 -> 6 -> 2 -> 3
        Assert.That(values, Is.EqualTo([1, 4, 6, 2, 3]));
    }

    #endregion
}
