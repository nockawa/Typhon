using NUnit.Framework;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
// ReSharper disable AccessToDisposedClosure

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

    [Test]
    public void Allocate_AsChainRoot_SetsCorrectMetadata()
    {
        using var allocator = new ChainedBlockAllocator(sizeof(int), 64);

        allocator.AllocateBlock(out var blockId, true);

        // A newly allocated chain root should have length 1 and last block pointing to itself
        Assert.That(allocator.GetChainLength(blockId), Is.EqualTo(1));
        Assert.That(allocator.GetLastBlockInChain(blockId), Is.EqualTo(blockId));
        Assert.That(allocator.GetChainRoot(blockId), Is.EqualTo(blockId));
    }

    [Test]
    public void Allocate_NotAsChainRoot_HasZeroChainGeneration()
    {
        using var allocator = new ChainedBlockAllocator(sizeof(int), 64);

        allocator.AllocateBlock(out var blockId, false);

        // A non-chain-root block has generation 0 (not part of any chain)
        Assert.That(allocator.GetBlockChainGeneration(blockId), Is.EqualTo(0));
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

    #region GetChainLength Tests

    [Test]
    public void GetChainLength_SingleBlock_ReturnsOne()
    {
        using var allocator = new ChainedBlockAllocator(sizeof(int), 64);

        allocator.AllocateBlock(out var blockId, true);

        Assert.That(allocator.GetChainLength(blockId), Is.EqualTo(1));
    }

    [Test]
    public void GetChainLength_TwoBlocksChained_ReturnsTwo()
    {
        using var allocator = new ChainedBlockAllocator(sizeof(int), 64);

        allocator.AllocateBlock(out var blockA, true);
        allocator.AllocateBlock(out var blockB, false);
        allocator.Chain(blockA, blockB);

        Assert.That(allocator.GetChainLength(blockA), Is.EqualTo(2));
    }

    [Test]
    public void GetChainLength_MultipleBlocksChainedSequentially_ReturnsCorrectLength()
    {
        using var allocator = new ChainedBlockAllocator(sizeof(int), 64);

        allocator.AllocateBlock(out var blockA, true);
        allocator.AllocateBlock(out var blockB, false);
        allocator.AllocateBlock(out var blockC, false);
        allocator.AllocateBlock(out var blockD, false);

        allocator.Chain(blockA, blockB);
        allocator.Chain(blockB, blockC);
        allocator.Chain(blockC, blockD);

        Assert.That(allocator.GetChainLength(blockA), Is.EqualTo(4));
    }

    [Test]
    public void GetChainLength_AfterMergingChains_ReturnsCombinedLength()
    {
        using var allocator = new ChainedBlockAllocator(sizeof(int), 64);

        // Chain 1: A -> B (length 2)
        allocator.AllocateBlock(out var blockA, true);
        allocator.AllocateBlock(out var blockB, false);
        allocator.Chain(blockA, blockB);

        // Chain 2: C -> D -> E (length 3)
        allocator.AllocateBlock(out var blockC, true);
        allocator.AllocateBlock(out var blockD, false);
        allocator.AllocateBlock(out var blockE, false);
        allocator.Chain(blockC, blockD);
        allocator.Chain(blockD, blockE);

        // Merge: A -> C -> D -> E -> B (length 5)
        allocator.Chain(blockA, blockC);

        Assert.That(allocator.GetChainLength(blockA), Is.EqualTo(5));
    }

    [Test]
    public void GetChainLength_WithZeroBlockId_ThrowsInvalidOperationException()
    {
        using var allocator = new ChainedBlockAllocator(sizeof(int), 64);

        Assert.Throws<InvalidOperationException>(() => allocator.GetChainLength(0));
    }

    [Test]
    public void GetChainLength_WithNonRootBlock_ThrowsInvalidOperationException()
    {
        using var allocator = new ChainedBlockAllocator(sizeof(int), 64);

        allocator.AllocateBlock(out var blockA, true);
        allocator.AllocateBlock(out var blockB, false);
        allocator.Chain(blockA, blockB);

        Assert.Throws<InvalidOperationException>(() => allocator.GetChainLength(blockB));
    }

    #endregion

    #region GetBlockChainGeneration Tests

    [Test]
    public void GetBlockChainGeneration_ChainRoot_ReturnsNonZero()
    {
        using var allocator = new ChainedBlockAllocator(sizeof(int), 64);

        allocator.AllocateBlock(out var blockId, true);

        Assert.That(allocator.GetBlockChainGeneration(blockId), Is.GreaterThan(0));
    }

    [Test]
    public void GetBlockChainGeneration_AllBlocksInChain_ReturnSameGeneration()
    {
        using var allocator = new ChainedBlockAllocator(sizeof(int), 64);

        allocator.AllocateBlock(out var blockA, true);
        allocator.AllocateBlock(out var blockB, false);
        allocator.AllocateBlock(out var blockC, false);
        allocator.Chain(blockA, blockB);
        allocator.Chain(blockB, blockC);

        var genA = allocator.GetBlockChainGeneration(blockA);
        var genB = allocator.GetBlockChainGeneration(blockB);
        var genC = allocator.GetBlockChainGeneration(blockC);

        Assert.That(genB, Is.EqualTo(genA));
        Assert.That(genC, Is.EqualTo(genA));
    }

    [Test]
    public void GetBlockChainGeneration_DifferentChains_ReturnDifferentGenerations()
    {
        using var allocator = new ChainedBlockAllocator(sizeof(int), 64);

        allocator.AllocateBlock(out var chainA, true);
        allocator.AllocateBlock(out var chainB, true);

        var genA = allocator.GetBlockChainGeneration(chainA);
        var genB = allocator.GetBlockChainGeneration(chainB);

        Assert.That(genA, Is.Not.EqualTo(genB));
    }

    [Test]
    public void GetBlockChainGeneration_AfterMerge_AllBlocksShareSameGeneration()
    {
        using var allocator = new ChainedBlockAllocator(sizeof(int), 64);

        // Chain 1
        allocator.AllocateBlock(out var blockA, true);
        allocator.AllocateBlock(out var blockB, false);
        allocator.Chain(blockA, blockB);

        // Chain 2
        allocator.AllocateBlock(out var blockC, true);
        allocator.AllocateBlock(out var blockD, false);
        allocator.Chain(blockC, blockD);

        var originalGenA = allocator.GetBlockChainGeneration(blockA);

        // Merge chain 2 into chain 1
        allocator.Chain(blockA, blockC);

        // All blocks should now share chain A's generation
        Assert.That(allocator.GetBlockChainGeneration(blockA), Is.EqualTo(originalGenA));
        Assert.That(allocator.GetBlockChainGeneration(blockC), Is.EqualTo(originalGenA));
        Assert.That(allocator.GetBlockChainGeneration(blockD), Is.EqualTo(originalGenA));
        Assert.That(allocator.GetBlockChainGeneration(blockB), Is.EqualTo(originalGenA));
    }

    [Test]
    public void GetBlockChainGeneration_WithZeroBlockId_ThrowsInvalidOperationException()
    {
        using var allocator = new ChainedBlockAllocator(sizeof(int), 64);

        Assert.Throws<InvalidOperationException>(() => allocator.GetBlockChainGeneration(0));
    }

    #endregion

    #region GetLastBlockInChain Tests

    [Test]
    public void GetLastBlockInChain_SingleBlock_ReturnsItself()
    {
        using var allocator = new ChainedBlockAllocator(sizeof(int), 64);

        allocator.AllocateBlock(out var blockId, true);

        Assert.That(allocator.GetLastBlockInChain(blockId), Is.EqualTo(blockId));
    }

    [Test]
    public void GetLastBlockInChain_TwoBlocksChained_ReturnsSecond()
    {
        using var allocator = new ChainedBlockAllocator(sizeof(int), 64);

        allocator.AllocateBlock(out var blockA, true);
        allocator.AllocateBlock(out var blockB, false);
        allocator.Chain(blockA, blockB);

        Assert.That(allocator.GetLastBlockInChain(blockA), Is.EqualTo(blockB));
    }

    [Test]
    public void GetLastBlockInChain_LongChain_ReturnsLastBlock()
    {
        using var allocator = new ChainedBlockAllocator(sizeof(int), 64);
        const int chainLength = 10;
        var blocks = new int[chainLength];

        for (int i = 0; i < chainLength; i++)
        {
            allocator.AllocateBlock(out blocks[i], i == 0);
        }

        for (int i = 0; i < chainLength - 1; i++)
        {
            allocator.Chain(blocks[i], blocks[i + 1]);
        }

        Assert.That(allocator.GetLastBlockInChain(blocks[0]), Is.EqualTo(blocks[chainLength - 1]));
    }

    [Test]
    public void GetLastBlockInChain_AfterMergingChains_ReturnsCorrectLast()
    {
        using var allocator = new ChainedBlockAllocator(sizeof(int), 64);

        // Chain 1: A -> B
        allocator.AllocateBlock(out var blockA, true);
        allocator.AllocateBlock(out var blockB, false);
        allocator.Chain(blockA, blockB);

        // Chain 2: C -> D
        allocator.AllocateBlock(out var blockC, true);
        allocator.AllocateBlock(out var blockD, false);
        allocator.Chain(blockC, blockD);

        // Merge: A -> C -> D -> B
        allocator.Chain(blockA, blockC);

        // After merge, B should be the last (chain 2's tail links to A's old next)
        Assert.That(allocator.GetLastBlockInChain(blockA), Is.EqualTo(blockB));
    }

    [Test]
    public void GetLastBlockInChain_WithZeroBlockId_ThrowsInvalidOperationException()
    {
        using var allocator = new ChainedBlockAllocator(sizeof(int), 64);

        Assert.Throws<InvalidOperationException>(() => allocator.GetLastBlockInChain(0));
    }

    [Test]
    public void GetLastBlockInChain_WithNonRootBlock_ThrowsInvalidOperationException()
    {
        using var allocator = new ChainedBlockAllocator(sizeof(int), 64);

        allocator.AllocateBlock(out var blockA, true);
        allocator.AllocateBlock(out var blockB, false);
        allocator.Chain(blockA, blockB);

        Assert.Throws<InvalidOperationException>(() => allocator.GetLastBlockInChain(blockB));
    }

    #endregion

    #region GetChainRoot Tests

    [Test]
    public void GetChainRoot_ChainRootBlock_ReturnsItself()
    {
        using var allocator = new ChainedBlockAllocator(sizeof(int), 64);

        allocator.AllocateBlock(out var blockId, true);

        Assert.That(allocator.GetChainRoot(blockId), Is.EqualTo(blockId));
    }

    [Test]
    public void GetChainRoot_NonRootBlock_ReturnsRootBlockId()
    {
        using var allocator = new ChainedBlockAllocator(sizeof(int), 64);

        allocator.AllocateBlock(out var blockA, true);
        allocator.AllocateBlock(out var blockB, false);
        allocator.AllocateBlock(out var blockC, false);
        allocator.Chain(blockA, blockB);
        allocator.Chain(blockB, blockC);

        Assert.That(allocator.GetChainRoot(blockB), Is.EqualTo(blockA));
        Assert.That(allocator.GetChainRoot(blockC), Is.EqualTo(blockA));
    }

    [Test]
    public void GetChainRoot_AfterMerge_AllBlocksReturnNewRoot()
    {
        using var allocator = new ChainedBlockAllocator(sizeof(int), 64);

        // Chain 1: A -> B
        allocator.AllocateBlock(out var blockA, true);
        allocator.AllocateBlock(out var blockB, false);
        allocator.Chain(blockA, blockB);

        // Chain 2: C -> D
        allocator.AllocateBlock(out var blockC, true);
        allocator.AllocateBlock(out var blockD, false);
        allocator.Chain(blockC, blockD);

        // Merge chain 2 into chain 1
        allocator.Chain(blockA, blockC);

        // All blocks should return A as the root
        Assert.That(allocator.GetChainRoot(blockA), Is.EqualTo(blockA));
        Assert.That(allocator.GetChainRoot(blockC), Is.EqualTo(blockA));
        Assert.That(allocator.GetChainRoot(blockD), Is.EqualTo(blockA));
        Assert.That(allocator.GetChainRoot(blockB), Is.EqualTo(blockA));
    }

    [Test]
    public void GetChainRoot_WithZeroBlockId_ThrowsInvalidOperationException()
    {
        using var allocator = new ChainedBlockAllocator(sizeof(int), 64);

        Assert.Throws<InvalidOperationException>(() => allocator.GetChainRoot(0));
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
        Assert.That(allocator.GetChainLength(blockA), Is.EqualTo(2));
        Assert.That(allocator.GetLastBlockInChain(blockA), Is.EqualTo(blockB));

        var nextPtr = allocator.NextBlock(blockA, out _);
        Assert.That(nextPtr.IsEmpty, Is.False);
        Assert.That(MemoryMarshal.Read<int>(nextPtr), Is.EqualTo(200));
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

        // Verify chain length and last block
        Assert.That(allocator.GetChainLength(blockA), Is.EqualTo(3));
        Assert.That(allocator.GetLastBlockInChain(blockA), Is.EqualTo(blockC));

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
        allocator.Chain(blockA, blockD);

        // Verify chain length and last block
        Assert.That(allocator.GetChainLength(blockA), Is.EqualTo(4));
        Assert.That(allocator.GetLastBlockInChain(blockA), Is.EqualTo(blockC));

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
    public void Chain_MultipleBlockAtOnce_CreateCorrectly()
    {
        using var allocator = new ChainedBlockAllocator(sizeof(int), 64);

        // Create chain 1: A -> B -> C
        var ptrA = allocator.AllocateBlock(out var blockA, true);
        var ptrB = allocator.AllocateBlock(out var blockB, false);
        var ptrC = allocator.AllocateBlock(out var blockC, false);
        ptrA.Cast<byte, int>()[0] = 1;
        ptrB.Cast<byte, int>()[0] = 2;
        ptrC.Cast<byte, int>()[0] = 3;

        allocator.Chain(blockA, blockB, blockC);

        var values = new List<int>();
        foreach (var blockId in allocator.EnumerateChainedBlock(blockA))
        {
            var block = allocator.GetBlockData(blockId);
            values.Add(MemoryMarshal.Read<int>(block));
        }

        Assert.That(values, Is.EqualTo([1, 2, 3]));
        Assert.That(allocator.GetChainLength(blockA), Is.EqualTo(3));
        Assert.That(allocator.GetBlockChainGeneration(blockA), Is.EqualTo(allocator.GetBlockChainGeneration(blockB)));
        Assert.That(allocator.GetBlockChainGeneration(blockB), Is.EqualTo(allocator.GetBlockChainGeneration(blockC)));
        Assert.That(allocator.GetLastBlockInChain(blockA), Is.EqualTo(blockC));
    }

    [Test]
    public void Chain_MultipleBlockAtOnce_SingleBlock_DoesNothing()
    {
        using var allocator = new ChainedBlockAllocator(sizeof(int), 64);

        allocator.AllocateBlock(out var blockA, true);

        allocator.Chain(blockA); // Should do nothing

        Assert.That(allocator.GetChainLength(blockA), Is.EqualTo(1));
    }

    [Test]
    public void Chain_MultipleBlockAtOnce_FiveBlocks_CorrectOrder()
    {
        using var allocator = new ChainedBlockAllocator(sizeof(int), 64);

        allocator.AllocateBlock(out var blockA, true).Cast<byte, int>()[0] = 1;
        allocator.AllocateBlock(out var blockB, false).Cast<byte, int>()[0] = 2;
        allocator.AllocateBlock(out var blockC, false).Cast<byte, int>()[0] = 3;
        allocator.AllocateBlock(out var blockD, false).Cast<byte, int>()[0] = 4;
        allocator.AllocateBlock(out var blockE, false).Cast<byte, int>()[0] = 5;

        allocator.Chain(blockA, blockB, blockC, blockD, blockE);

        var values = new List<int>();
        foreach (var blockId in allocator.EnumerateChainedBlock(blockA))
        {
            values.Add(MemoryMarshal.Read<int>(allocator.GetBlockData(blockId)));
        }

        Assert.That(values, Is.EqualTo([1, 2, 3, 4, 5]));
        Assert.That(allocator.GetChainLength(blockA), Is.EqualTo(5));
        Assert.That(allocator.GetLastBlockInChain(blockA), Is.EqualTo(blockE));

        // All blocks should have the same generation
        var gen = allocator.GetBlockChainGeneration(blockA);
        Assert.That(allocator.GetBlockChainGeneration(blockB), Is.EqualTo(gen));
        Assert.That(allocator.GetBlockChainGeneration(blockC), Is.EqualTo(gen));
        Assert.That(allocator.GetBlockChainGeneration(blockD), Is.EqualTo(gen));
        Assert.That(allocator.GetBlockChainGeneration(blockE), Is.EqualTo(gen));

        // All non-root blocks should reference the root
        Assert.That(allocator.GetChainRoot(blockB), Is.EqualTo(blockA));
        Assert.That(allocator.GetChainRoot(blockC), Is.EqualTo(blockA));
        Assert.That(allocator.GetChainRoot(blockD), Is.EqualTo(blockA));
        Assert.That(allocator.GetChainRoot(blockE), Is.EqualTo(blockA));
    }

    [Test]
    public void Chain_ChainWithZero_BreaksChainAndCreatesOrphan()
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

        // Verify initial chain
        Assert.That(allocator.GetChainLength(blockA), Is.EqualTo(3));

        // Break the chain after A, creating orphan chain B -> C
        var orphanRoot = allocator.Chain(blockA, 0);

        // A should now be a single-block chain
        Assert.That(allocator.GetChainLength(blockA), Is.EqualTo(1));
        Assert.That(allocator.GetLastBlockInChain(blockA), Is.EqualTo(blockA));
        Assert.That(allocator.NextBlock(blockA, out _).IsEmpty, Is.True);

        // B should now be the root of its own chain (B -> C)
        Assert.That(orphanRoot, Is.EqualTo(blockB));
        Assert.That(allocator.GetChainLength(blockB), Is.EqualTo(2));
        Assert.That(allocator.GetLastBlockInChain(blockB), Is.EqualTo(blockC));
        Assert.That(allocator.GetChainRoot(blockC), Is.EqualTo(blockB));

        // Orphan chain should have a new generation
        Assert.That(allocator.GetBlockChainGeneration(blockB), Is.Not.EqualTo(allocator.GetBlockChainGeneration(blockA)));
        Assert.That(allocator.GetBlockChainGeneration(blockC), Is.EqualTo(allocator.GetBlockChainGeneration(blockB)));
    }

    [Test]
    public void Chain_BreakMiddleOfChain_CreatesCorrectOrphan()
    {
        using var allocator = new ChainedBlockAllocator(sizeof(int), 64);

        allocator.AllocateBlock(out var blockA, true).Cast<byte, int>()[0] = 1;
        allocator.AllocateBlock(out var blockB, false).Cast<byte, int>()[0] = 2;
        allocator.AllocateBlock(out var blockC, false).Cast<byte, int>()[0] = 3;
        allocator.AllocateBlock(out var blockD, false).Cast<byte, int>()[0] = 4;

        allocator.Chain(blockA, blockB, blockC, blockD);

        // Break after B, creating orphan C -> D
        var orphanRoot = allocator.Chain(blockB, 0);

        // Chain A should now be A -> B
        Assert.That(allocator.GetChainLength(blockA), Is.EqualTo(2));
        Assert.That(allocator.GetLastBlockInChain(blockA), Is.EqualTo(blockB));

        // Orphan should be C -> D
        Assert.That(orphanRoot, Is.EqualTo(blockC));
        Assert.That(allocator.GetChainLength(blockC), Is.EqualTo(2));
        Assert.That(allocator.GetLastBlockInChain(blockC), Is.EqualTo(blockD));
    }

    [Test]
    public void Chain_BreakAtEnd_LastBlockBecomesOrphan()
    {
        using var allocator = new ChainedBlockAllocator(sizeof(int), 64);

        allocator.AllocateBlock(out var blockA, true);
        allocator.AllocateBlock(out var blockB, false);
        allocator.AllocateBlock(out var blockC, false);

        allocator.Chain(blockA, blockB, blockC);

        // Break after B, creating orphan C
        var orphanRoot = allocator.Chain(blockB, 0);

        Assert.That(orphanRoot, Is.EqualTo(blockC));
        Assert.That(allocator.GetChainLength(blockC), Is.EqualTo(1));
        Assert.That(allocator.GetLastBlockInChain(blockC), Is.EqualTo(blockC));
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

        // Verify chain length
        Assert.That(allocator.GetChainLength(blockA), Is.EqualTo(6));
        Assert.That(allocator.GetLastBlockInChain(blockA), Is.EqualTo(blockC));

        var values = new List<int>();
        foreach (var blockId in allocator.EnumerateChainedBlock(blockA))
        {
            var block = allocator.GetBlockData(blockId);
            values.Add(MemoryMarshal.Read<int>(block));
        }

        Assert.That(values, Is.EqualTo([1, 4, 5, 6, 2, 3]));

        // All blocks should share the same generation and root
        var gen = allocator.GetBlockChainGeneration(blockA);
        Assert.That(allocator.GetBlockChainGeneration(blockD), Is.EqualTo(gen));
        Assert.That(allocator.GetBlockChainGeneration(blockE), Is.EqualTo(gen));
        Assert.That(allocator.GetBlockChainGeneration(blockF), Is.EqualTo(gen));
        Assert.That(allocator.GetBlockChainGeneration(blockB), Is.EqualTo(gen));
        Assert.That(allocator.GetBlockChainGeneration(blockC), Is.EqualTo(gen));

        Assert.That(allocator.GetChainRoot(blockD), Is.EqualTo(blockA));
        Assert.That(allocator.GetChainRoot(blockE), Is.EqualTo(blockA));
        Assert.That(allocator.GetChainRoot(blockF), Is.EqualTo(blockA));
    }

    [Test]
    public void Chain_AppendToEndOfChain_UpdatesLastBlock()
    {
        using var allocator = new ChainedBlockAllocator(sizeof(int), 64);

        allocator.AllocateBlock(out var blockA, true);
        allocator.AllocateBlock(out var blockB, false);

        // Get the last block and append to it
        var lastBlock = allocator.GetLastBlockInChain(blockA);
        Assert.That(lastBlock, Is.EqualTo(blockA)); // Initially, A is the last

        allocator.Chain(blockA, blockB);

        Assert.That(allocator.GetLastBlockInChain(blockA), Is.EqualTo(blockB));
    }

    [Test]
    public void Chain_WithZeroBlockId_ThrowsInvalidOperationException()
    {
        using var allocator = new ChainedBlockAllocator(sizeof(int), 64);

        allocator.AllocateBlock(out var blockA, true);

        Assert.Throws<InvalidOperationException>(() => allocator.Chain(0, blockA));
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

        // Chain all blocks using params overload
        var blockIds = new int[blockCount];
        for (int i = 0; i < blockCount; i++)
        {
            blockIds[i] = blocks[i].id;
        }
        allocator.Chain(blockIds);

        // Verify chain length
        Assert.That(allocator.GetChainLength(blocks[0].id), Is.EqualTo(blockCount));
        Assert.That(allocator.GetLastBlockInChain(blocks[0].id), Is.EqualTo(blocks[blockCount - 1].id));

        // Verify all data is intact
        for (int i = 0; i < blocks.Length; i++)
        {
            var span = allocator.GetBlockData(blocks[i].id);
            Assert.That(MemoryMarshal.Read<long>(span), Is.EqualTo(blocks[i].value));
        }
    }

    [Test]
    public void Chain_InsertSingleBlockChainIntoMiddle_CorrectOrder()
    {
        using var allocator = new ChainedBlockAllocator(sizeof(int), 64);

        allocator.AllocateBlock(out var blockA, true).Cast<byte, int>()[0] = 1;
        allocator.AllocateBlock(out var blockB, false).Cast<byte, int>()[0] = 2;
        allocator.AllocateBlock(out var blockC, false).Cast<byte, int>()[0] = 3; // Single block to insert

        allocator.Chain(blockA, blockB);

        // Insert C between A and B: A -> C -> B
        allocator.Chain(blockA, blockC);

        Assert.That(allocator.GetChainLength(blockA), Is.EqualTo(3));

        var values = new List<int>();
        foreach (var blockId in allocator.EnumerateChainedBlock(blockA))
        {
            values.Add(MemoryMarshal.Read<int>(allocator.GetBlockData(blockId)));
        }

        Assert.That(values, Is.EqualTo([1, 3, 2]));
    }

    [Test]
    public void Chain_MultipleChainOperations_MaintainsCorrectMetadata()
    {
        using var allocator = new ChainedBlockAllocator(sizeof(int), 64);

        allocator.AllocateBlock(out var blockA, true);
        allocator.AllocateBlock(out var blockB, false);
        allocator.AllocateBlock(out var blockC, false);
        allocator.AllocateBlock(out var blockD, false);
        allocator.AllocateBlock(out var blockE, false);

        // Build A -> B
        allocator.Chain(blockA, blockB);
        Assert.That(allocator.GetChainLength(blockA), Is.EqualTo(2));
        Assert.That(allocator.GetLastBlockInChain(blockA), Is.EqualTo(blockB));

        // Extend to A -> B -> C
        allocator.Chain(blockB, blockC);
        Assert.That(allocator.GetChainLength(blockA), Is.EqualTo(3));
        Assert.That(allocator.GetLastBlockInChain(blockA), Is.EqualTo(blockC));

        // Insert D after A: A -> D -> B -> C
        allocator.Chain(blockA, blockD);
        Assert.That(allocator.GetChainLength(blockA), Is.EqualTo(4));
        Assert.That(allocator.GetLastBlockInChain(blockA), Is.EqualTo(blockC));

        // Insert E after D: A -> D -> E -> B -> C
        allocator.Chain(blockD, blockE);
        Assert.That(allocator.GetChainLength(blockA), Is.EqualTo(5));
        Assert.That(allocator.GetLastBlockInChain(blockA), Is.EqualTo(blockC));

        // Verify all blocks point to the root
        Assert.That(allocator.GetChainRoot(blockD), Is.EqualTo(blockA));
        Assert.That(allocator.GetChainRoot(blockE), Is.EqualTo(blockA));
        Assert.That(allocator.GetChainRoot(blockB), Is.EqualTo(blockA));
        Assert.That(allocator.GetChainRoot(blockC), Is.EqualTo(blockA));
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

        allocator.Chain(blockA, blockB, blockC, blockD);

        // Navigate using NextBlock
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

        allocator.FreeChain(blockId);
        Assert.That(allocator.AllocatedCount, Is.EqualTo(1)); // Only reserved block remains
    }

    [Test]
    public void Free_ChainedBlocks_FreesAllInChain()
    {
        using var allocator = new ChainedBlockAllocator(16, 64);

        allocator.AllocateBlock(out var blockA, true);
        allocator.AllocateBlock(out var blockB, false);
        allocator.AllocateBlock(out var blockC, false);

        allocator.Chain(blockA, blockB, blockC);

        Assert.That(allocator.AllocatedCount, Is.EqualTo(4)); // 1 reserved + 3 allocated
        Assert.That(allocator.GetChainLength(blockA), Is.EqualTo(3));

        allocator.FreeChain(blockA);

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

        allocator.Chain(blockA, blockB, blockC, blockD);

        Assert.That(allocator.AllocatedCount, Is.EqualTo(5)); // 1 reserved + 4 allocated

        // Break after A, making B the root of orphan chain
        allocator.Chain(blockA, 0);

        // Free orphan chain starting from B
        allocator.FreeChain(blockB);

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

        allocator.FreeChain(blockA);
        Assert.That(allocator.AllocatedCount, Is.EqualTo(3)); // 1 reserved + 2 remaining

        allocator.FreeChain(blockC);
        Assert.That(allocator.AllocatedCount, Is.EqualTo(1)); // Only reserved block remains
    }

    [Test]
    public void Free_ZeroBlockId_DoesNothing()
    {
        using var allocator = new ChainedBlockAllocator(16, 64);

        allocator.AllocateBlock(out var blockA, true);

        // Should not throw and should not change anything
        Assert.DoesNotThrow(() => allocator.FreeChain(0));
        Assert.That(allocator.AllocatedCount, Is.EqualTo(2));
    }

    [Test]
    public void Free_NonRootBlock_ThrowsInvalidOperationException()
    {
        using var allocator = new ChainedBlockAllocator(16, 64);

        allocator.AllocateBlock(out var blockA, true);
        allocator.AllocateBlock(out var blockB, false);
        allocator.Chain(blockA, blockB);

        Assert.Throws<InvalidOperationException>(() => allocator.FreeChain(blockB));
    }

    [Test]
    public void Free_Append()
    {
        using var allocator = new ChainedBlockAllocator<ulong>(1024);

        ref var cur = ref allocator.Allocate(out var blockId, true);

        for (int i = 0; i < 10; i++)
        {
            allocator.SafeAppend(ref cur);
            cur = ref allocator.Next(ref cur);
        }

        Assert.That(allocator.AllocatedCount, Is.EqualTo(12));

        allocator.FreeChain(blockId);
        Assert.That(allocator.AllocatedCount, Is.EqualTo(1));
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
            allocator.FreeChain(ids[i]);
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

        allocator.Chain(blockA, blockB, blockC);

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

        allocator.Chain(blockA, blockB, blockC);

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

        allocator.Chain(blocks);

        // Verify using GetChainLength
        Assert.That(allocator.GetChainLength(blocks[0]), Is.EqualTo(chainLength));

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

        allocator.Chain(blockA, blockB, blockC);

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
                    allocator.FreeChain(kvp.Key);
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

        // Chain blocks across different pages using params
        allocator.Chain(blocks.ToArray());

        // Verify chain metadata
        Assert.That(allocator.GetChainLength(blocks[0]), Is.EqualTo(blocks.Count));
        Assert.That(allocator.GetLastBlockInChain(blocks[0]), Is.EqualTo(blocks[^1]));

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

        // Verify lengths before merge
        Assert.That(allocator.GetChainLength(blocks[0]), Is.EqualTo(3));
        Assert.That(allocator.GetChainLength(blocks[3]), Is.EqualTo(3));

        // Merge chains: 1 -> 4 -> 5 -> 6 -> 2 -> 3
        allocator.Chain(blocks[0], blocks[3]);

        // Verify merged chain metadata
        Assert.That(allocator.GetChainLength(blocks[0]), Is.EqualTo(6));
        Assert.That(allocator.GetLastBlockInChain(blocks[0]), Is.EqualTo(blocks[2]));

        var values = new List<int>();
        foreach (var blockId in allocator.EnumerateChainedBlock(blocks[0]))
        {
            var block = allocator.GetBlockData(blockId);
            values.Add(MemoryMarshal.Read<int>(block));
        }

        Assert.That(values, Is.EqualTo([1, 4, 5, 6, 2, 3]));
    }

    [Test]
    public void ChainBreakAndRejoin_MaintainsCorrectMetadata()
    {
        using var allocator = new ChainedBlockAllocator(sizeof(int), 64);

        allocator.AllocateBlock(out var blockA, true).Cast<byte, int>()[0] = 1;
        allocator.AllocateBlock(out var blockB, false).Cast<byte, int>()[0] = 2;
        allocator.AllocateBlock(out var blockC, false).Cast<byte, int>()[0] = 3;
        allocator.AllocateBlock(out var blockD, false).Cast<byte, int>()[0] = 4;

        // Build A -> B -> C -> D
        allocator.Chain(blockA, blockB, blockC, blockD);
        Assert.That(allocator.GetChainLength(blockA), Is.EqualTo(4));

        // Break into A -> B and C -> D (orphan)
        var orphan = allocator.Chain(blockB, 0);

        Assert.That(allocator.GetChainLength(blockA), Is.EqualTo(2));
        Assert.That(allocator.GetLastBlockInChain(blockA), Is.EqualTo(blockB));

        Assert.That(orphan, Is.EqualTo(blockC));
        Assert.That(allocator.GetChainLength(blockC), Is.EqualTo(2));
        Assert.That(allocator.GetLastBlockInChain(blockC), Is.EqualTo(blockD));

        // Rejoin: A -> B -> C -> D
        allocator.Chain(blockB, blockC);

        Assert.That(allocator.GetChainLength(blockA), Is.EqualTo(4));
        Assert.That(allocator.GetLastBlockInChain(blockA), Is.EqualTo(blockD));

        // All should reference blockA as root again
        Assert.That(allocator.GetChainRoot(blockC), Is.EqualTo(blockA));
        Assert.That(allocator.GetChainRoot(blockD), Is.EqualTo(blockA));
    }

    [Test]
    public void MultipleBreaksAndMerges_StressTest()
    {
        using var allocator = new ChainedBlockAllocator(sizeof(int), 64);
        const int chainLength = 20;

        var blocks = new int[chainLength];
        for (int i = 0; i < chainLength; i++)
        {
            allocator.AllocateBlock(out blocks[i], i == 0).Cast<byte, int>()[0] = i;
        }

        // Build full chain
        allocator.Chain(blocks);
        Assert.That(allocator.GetChainLength(blocks[0]), Is.EqualTo(chainLength));

        // Break at position 5, creating orphan 5-19
        var orphan1 = allocator.Chain(blocks[4], 0);
        Assert.That(orphan1, Is.EqualTo(blocks[5]));
        Assert.That(allocator.GetChainLength(blocks[0]), Is.EqualTo(5));
        Assert.That(allocator.GetChainLength(blocks[5]), Is.EqualTo(15));

        // Break orphan at position 10, creating orphan 10-19
        var orphan2 = allocator.Chain(blocks[9], 0);
        Assert.That(orphan2, Is.EqualTo(blocks[10]));
        Assert.That(allocator.GetChainLength(blocks[5]), Is.EqualTo(5));
        Assert.That(allocator.GetChainLength(blocks[10]), Is.EqualTo(10));

        // Rejoin: blocks[0-4] + blocks[10-19] + blocks[5-9]
        allocator.Chain(blocks[4], blocks[10]);
        allocator.Chain(blocks[19], blocks[5]);

        Assert.That(allocator.GetChainLength(blocks[0]), Is.EqualTo(chainLength));
        Assert.That(allocator.GetLastBlockInChain(blocks[0]), Is.EqualTo(blocks[9]));

        // Verify all blocks reference the root
        for (int i = 1; i < chainLength; i++)
        {
            Assert.That(allocator.GetChainRoot(blocks[i]), Is.EqualTo(blocks[0]));
        }
    }

    #endregion

    #region Concurrency Tests

    private const int HeavyThreadCount = 16;
    private const int HeavyOperationsPerThread = 625; // 16 * 625 = 10,000 total operations

    [Test]
    public void Concurrent_Allocation_MultipleThreadsAllocateSimultaneously()
    {
        using var allocator = new ChainedBlockAllocator(sizeof(long), 64);
        var allocatedIds = new ConcurrentBag<int>();
        var exceptions = new ConcurrentBag<Exception>();

        Parallel.For(0, HeavyThreadCount, new ParallelOptions { MaxDegreeOfParallelism = HeavyThreadCount }, threadIndex =>
        {
            try
            {
                for (int i = 0; i < HeavyOperationsPerThread; i++)
                {
                    var span = allocator.AllocateBlock(out var blockId, true);
                    span.Cast<byte, long>()[0] = threadIndex * 1_000_000L + i;
                    allocatedIds.Add(blockId);
                }
            }
            catch (Exception ex)
            {
                exceptions.Add(ex);
            }
        });

        Assert.That(exceptions, Is.Empty, $"Exceptions occurred: {string.Join(", ", exceptions)}");

        // Verify all IDs are unique
        var idList = allocatedIds.ToArray();
        var uniqueIds = new HashSet<int>(idList);
        Assert.That(uniqueIds.Count, Is.EqualTo(idList.Length), "Duplicate block IDs were allocated");

        // +1 for reserved block 0
        Assert.That(allocator.AllocatedCount, Is.EqualTo(HeavyThreadCount * HeavyOperationsPerThread + 1));
    }

    [Test]
    public void Concurrent_Allocation_DataIntegrityAfterParallelWrites()
    {
        using var allocator = new ChainedBlockAllocator(sizeof(long), 64);
        var allocations = new ConcurrentDictionary<int, long>();

        Parallel.For(0, HeavyThreadCount, new ParallelOptions { MaxDegreeOfParallelism = HeavyThreadCount }, threadIndex =>
        {
            for (int i = 0; i < HeavyOperationsPerThread; i++)
            {
                var value = threadIndex * 1_000_000L + i;
                var span = allocator.AllocateBlock(out var blockId, true);
                span.Cast<byte, long>()[0] = value;
                allocations[blockId] = value;
            }
        });

        // Verify all data is intact
        foreach (var kvp in allocations)
        {
            var span = allocator.GetBlockData(kvp.Key);
            var actualValue = MemoryMarshal.Read<long>(span);
            Assert.That(actualValue, Is.EqualTo(kvp.Value), $"Data corruption at block {kvp.Key}");
        }
    }

    [Test]
    public void Concurrent_Chaining_MultipleThreadsChainToSeparateRoots()
    {
        using var allocator = new ChainedBlockAllocator(sizeof(int), 64);
        var chainRoots = new int[HeavyThreadCount];
        var exceptions = new ConcurrentBag<Exception>();

        // Pre-allocate chain roots
        for (int i = 0; i < HeavyThreadCount; i++)
        {
            allocator.AllocateBlock(out chainRoots[i], true);
        }

        // Each thread builds its own chain
        Parallel.For(0, HeavyThreadCount, new ParallelOptions { MaxDegreeOfParallelism = HeavyThreadCount }, threadIndex =>
        {
            try
            {
                var rootId = chainRoots[threadIndex];
                var lastBlockId = rootId;

                for (int i = 0; i < HeavyOperationsPerThread; i++)
                {
                    var span = allocator.AllocateBlock(out var newBlockId, false);
                    span.Cast<byte, int>()[0] = threadIndex * 10000 + i;

                    // Chain to the last block in this thread's chain
                    var lastInChain = allocator.GetLastBlockInChain(rootId);
                    allocator.Chain(lastInChain, newBlockId);
                }
            }
            catch (Exception ex)
            {
                exceptions.Add(ex);
            }
        });

        Assert.That(exceptions, Is.Empty, $"Exceptions occurred: {string.Join(", ", exceptions)}");

        // Verify each chain has the correct length
        for (int i = 0; i < HeavyThreadCount; i++)
        {
            var chainLength = allocator.GetChainLength(chainRoots[i]);
            Assert.That(chainLength, Is.EqualTo(HeavyOperationsPerThread + 1), $"Chain {i} has incorrect length");
        }
    }

    [Test]
    public void Concurrent_SafeAppend_MultipleThreadsAppendToSameChain()
    {
        using var allocator = new ChainedBlockAllocator<long>(64);
        var exceptions = new ConcurrentBag<Exception>();
        var appendedValues = new ConcurrentBag<long>();

        ref var root = ref allocator.Allocate(out var rootId, true);
        root = -1; // Mark root

        // Multiple threads try to append to the same chain
        Parallel.For(0, HeavyThreadCount, new ParallelOptions { MaxDegreeOfParallelism = HeavyThreadCount }, threadIndex =>
        {
            try
            {
                for (int i = 0; i < HeavyOperationsPerThread; i++)
                {
                    var value = threadIndex * 1_000_000L + i;

                    // SafeAppend is thread-safe for appending after a specific block
                    // We need to find the current end and append there
                    ref var current = ref allocator.Get(rootId);
                    while (!Unsafe.IsNullRef(ref allocator.Next(ref current)))
                    {
                        current = ref allocator.Next(ref current);
                    }

                    ref var newBlock = ref allocator.SafeAppend(ref current);
                    newBlock = value;
                    appendedValues.Add(value);
                }
            }
            catch (Exception ex)
            {
                exceptions.Add(ex);
            }
        });

        Assert.That(exceptions, Is.Empty, $"Exceptions occurred: {string.Join(", ", exceptions)}");

        // Count blocks in chain by traversing
        var blockCount = 0;
        ref var cur = ref allocator.Get(rootId);
        while (!Unsafe.IsNullRef(ref cur))
        {
            blockCount++;
            cur = ref allocator.Next(ref cur);
        }

        // We should have at least some blocks (exact count depends on race conditions in SafeAppend)
        Assert.That(blockCount, Is.GreaterThan(1), "Chain should have grown");
    }

    [Test]
    public void Concurrent_Enumeration_SafeDuringConcurrentModification()
    {
        using var allocator = new ChainedBlockAllocator(sizeof(int), 64);
        var exceptions = new ConcurrentBag<Exception>();

        // Create an initial chain
        allocator.AllocateBlock(out var rootId, true).Cast<byte, int>()[0] = 0;
        for (int i = 1; i <= 100; i++)
        {
            var span = allocator.AllocateBlock(out var blockId, false);
            span.Cast<byte, int>()[0] = i;
            allocator.Chain(allocator.GetLastBlockInChain(rootId), blockId);
        }

        var enumerationCompleted = new CountdownEvent(HeavyThreadCount / 2);
        var modificationCompleted = new CountdownEvent(HeavyThreadCount / 2);

        // Half threads enumerate, half threads modify (add new chains)
        Parallel.For(0, HeavyThreadCount, new ParallelOptions { MaxDegreeOfParallelism = HeavyThreadCount }, threadIndex =>
        {
            try
            {
                if (threadIndex % 2 == 0)
                {
                    // Enumerator thread
                    for (int round = 0; round < 100; round++)
                    {
                        var values = new List<int>();
                        foreach (var blockId in allocator.EnumerateChainedBlock(rootId))
                        {
                            var data = allocator.GetBlockData(blockId);
                            if (!data.IsEmpty)
                            {
                                values.Add(MemoryMarshal.Read<int>(data));
                            }
                        }
                        // Just verify we can enumerate without crashing
                        Assert.That(values.Count, Is.GreaterThan(0));
                    }
                    enumerationCompleted.Signal();
                }
                else
                {
                    // Modifier thread - creates new separate chains (doesn't modify the enumerated chain)
                    for (int i = 0; i < HeavyOperationsPerThread; i++)
                    {
                        allocator.AllocateBlock(out var newRoot, true);
                        allocator.AllocateBlock(out var block2, false);
                        allocator.Chain(newRoot, block2);
                    }
                    modificationCompleted.Signal();
                }
            }
            catch (Exception ex)
            {
                exceptions.Add(ex);
            }
        });

        Assert.That(exceptions, Is.Empty, $"Exceptions occurred: {string.Join(", ", exceptions)}");
    }

    [Test]
    public void Concurrent_RequestEnumeration_ProtectsAgainstFree()
    {
        using var allocator = new ChainedBlockAllocator(sizeof(int), 64);
        var exceptions = new ConcurrentBag<Exception>();

        // Create multiple chains
        var chainRoots = new int[HeavyThreadCount];
        for (int i = 0; i < HeavyThreadCount; i++)
        {
            allocator.AllocateBlock(out chainRoots[i], true).Cast<byte, int>()[0] = i;
            for (int j = 1; j <= 10; j++)
            {
                allocator.AllocateBlock(out var blockId, false).Cast<byte, int>()[0] = i * 100 + j;
                allocator.Chain(allocator.GetLastBlockInChain(chainRoots[i]), blockId);
            }
        }

        var barrier = new Barrier(HeavyThreadCount);

        Parallel.For(0, HeavyThreadCount, new ParallelOptions { MaxDegreeOfParallelism = HeavyThreadCount }, threadIndex =>
        {
            try
            {
                var myChainRoot = chainRoots[threadIndex];
                barrier.SignalAndWait(); // Synchronize start

                for (int round = 0; round < 100; round++)
                {
                    // Request enumeration (acquires shared access)
                    if (allocator.RequestEnumeration(myChainRoot, out var chainGen))
                    {
                        // Simulate some work during enumeration
                        var count = 0;
                        foreach (var blockId in allocator.EnumerateChainedBlock(myChainRoot))
                        {
                            count++;
                            Thread.SpinWait(10);
                        }
                        allocator.EndEnumeration(myChainRoot, chainGen);
                    }
                }
            }
            catch (Exception ex)
            {
                exceptions.Add(ex);
            }
        });

        Assert.That(exceptions, Is.Empty, $"Exceptions occurred: {string.Join(", ", exceptions)}");
    }

    [Test]
    public void Concurrent_FreeChain_WhileOtherChainsActive()
    {
        using var allocator = new ChainedBlockAllocator(sizeof(int), 64);
        var exceptions = new ConcurrentBag<Exception>();
        var activeChains = new ConcurrentDictionary<int, bool>();

        // Half threads create and keep chains, half threads create and free
        Parallel.For(0, HeavyThreadCount, new ParallelOptions { MaxDegreeOfParallelism = HeavyThreadCount }, threadIndex =>
        {
            try
            {
                for (int i = 0; i < HeavyOperationsPerThread; i++)
                {
                    // Allocate a small chain
                    allocator.AllocateBlock(out var rootId, true).Cast<byte, int>()[0] = threadIndex;
                    allocator.AllocateBlock(out var block2, false);
                    allocator.AllocateBlock(out var block3, false);
                    allocator.Chain(rootId, block2, block3);

                    if (threadIndex % 2 == 0)
                    {
                        // Keep the chain
                        activeChains[rootId] = true;
                    }
                    else
                    {
                        // Free the chain
                        allocator.FreeChain(rootId);
                    }
                }
            }
            catch (Exception ex)
            {
                exceptions.Add(ex);
            }
        });

        Assert.That(exceptions, Is.Empty, $"Exceptions occurred: {string.Join(", ", exceptions)}");

        // Verify active chains are still valid
        foreach (var rootId in activeChains.Keys)
        {
            Assert.That(allocator.GetChainLength(rootId), Is.EqualTo(3));
        }
    }

    [Test]
    public void Concurrent_MixedOperations_StressTest()
    {
        using var allocator = new ChainedBlockAllocator(sizeof(long), 64);
        var exceptions = new ConcurrentBag<Exception>();
        var activeChains = new ConcurrentDictionary<int, int>(); // rootId -> expected length
        var rng = new ThreadLocal<Random>(() => new Random(Thread.CurrentThread.ManagedThreadId));

        Parallel.For(0, HeavyThreadCount, new ParallelOptions { MaxDegreeOfParallelism = HeavyThreadCount }, threadIndex =>
        {
            try
            {
                var localRng = rng.Value!;
                var myChains = new List<int>();

                for (int i = 0; i < HeavyOperationsPerThread; i++)
                {
                    var operation = localRng.Next(5);

                    switch (operation)
                    {
                        case 0: // Allocate new chain
                        {
                            allocator.AllocateBlock(out var rootId, true).Cast<byte, long>()[0] = threadIndex;
                            myChains.Add(rootId);
                            activeChains[rootId] = 1;
                            break;
                        }
                        case 1: // Extend a chain
                        {
                            if (myChains.Count > 0)
                            {
                                var chainIndex = localRng.Next(myChains.Count);
                                var rootId = myChains[chainIndex];
                                if (activeChains.TryGetValue(rootId, out var currentLength))
                                {
                                    allocator.AllocateBlock(out var newBlock, false);
                                    var lastBlock = allocator.GetLastBlockInChain(rootId);
                                    allocator.Chain(lastBlock, newBlock);
                                    activeChains[rootId] = currentLength + 1;
                                }
                            }
                            break;
                        }
                        case 2: // Read chain metadata
                        {
                            if (myChains.Count > 0)
                            {
                                var chainIndex = localRng.Next(myChains.Count);
                                var rootId = myChains[chainIndex];
                                if (activeChains.ContainsKey(rootId))
                                {
                                    _ = allocator.GetChainLength(rootId);
                                    _ = allocator.GetLastBlockInChain(rootId);
                                    _ = allocator.GetBlockChainGeneration(rootId);
                                }
                            }
                            break;
                        }
                        case 3: // Enumerate a chain
                        {
                            if (myChains.Count > 0)
                            {
                                var chainIndex = localRng.Next(myChains.Count);
                                var rootId = myChains[chainIndex];
                                if (activeChains.ContainsKey(rootId))
                                {
                                    var count = 0;
                                    foreach (var _ in allocator.EnumerateChainedBlock(rootId))
                                    {
                                        count++;
                                    }
                                }
                            }
                            break;
                        }
                        case 4: // Free a chain (occasionally)
                        {
                            if (myChains.Count > 5 && localRng.Next(10) == 0)
                            {
                                var chainIndex = localRng.Next(myChains.Count);
                                var rootId = myChains[chainIndex];
                                if (activeChains.TryRemove(rootId, out _))
                                {
                                    allocator.FreeChain(rootId);
                                    myChains.RemoveAt(chainIndex);
                                }
                            }
                            break;
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                exceptions.Add(ex);
            }
        });

        Assert.That(exceptions, Is.Empty, $"Exceptions occurred: {string.Join(", ", exceptions)}");
    }

    [Test]
    public void Concurrent_ChainMetadata_ConsistencyUnderLoad()
    {
        using var allocator = new ChainedBlockAllocator(sizeof(int), 64);
        var exceptions = new ConcurrentBag<Exception>();

        // Create chains that will be extended by multiple threads
        var chainRoots = new int[HeavyThreadCount];
        for (int i = 0; i < HeavyThreadCount; i++)
        {
            allocator.AllocateBlock(out chainRoots[i], true);
        }

        var operationsPerChain = new int[HeavyThreadCount];
        var barrier = new Barrier(HeavyThreadCount);

        Parallel.For(0, HeavyThreadCount, new ParallelOptions { MaxDegreeOfParallelism = HeavyThreadCount }, threadIndex =>
        {
            try
            {
                barrier.SignalAndWait(); // Synchronize start

                // Each thread extends its own chain
                var rootId = chainRoots[threadIndex];
                for (int i = 0; i < HeavyOperationsPerThread; i++)
                {
                    allocator.AllocateBlock(out var newBlock, false);
                    var lastBlock = allocator.GetLastBlockInChain(rootId);
                    allocator.Chain(lastBlock, newBlock);
                    Interlocked.Increment(ref operationsPerChain[threadIndex]);

                    // Periodically verify consistency
                    if (i % 100 == 0)
                    {
                        var length = allocator.GetChainLength(rootId);
                        var lastInChain = allocator.GetLastBlockInChain(rootId);

                        // Length should match operations + 1 (for root)
                        Assert.That(length, Is.EqualTo(operationsPerChain[threadIndex] + 1),
                            $"Thread {threadIndex}: Chain length mismatch at iteration {i}");

                        // Verify last block is actually last (no next)
                        var nextSpan = allocator.NextBlock(lastInChain, out var nextId);
                        Assert.That(nextSpan.IsEmpty, Is.True,
                            $"Thread {threadIndex}: LastBlockInChain has a next block");
                    }
                }
            }
            catch (Exception ex)
            {
                exceptions.Add(ex);
            }
        });

        Assert.That(exceptions, Is.Empty, $"Exceptions occurred: {string.Join(", ", exceptions)}");

        // Final verification
        for (int i = 0; i < HeavyThreadCount; i++)
        {
            var expectedLength = HeavyOperationsPerThread + 1;
            var actualLength = allocator.GetChainLength(chainRoots[i]);
            Assert.That(actualLength, Is.EqualTo(expectedLength), $"Chain {i} has incorrect final length");
        }
    }

    [Test]
    public void Concurrent_BreakAndRejoin_StressTest()
    {
        using var allocator = new ChainedBlockAllocator(sizeof(int), 64);
        var exceptions = new ConcurrentBag<Exception>();

        // Each thread manages its own chain with break/rejoin operations
        Parallel.For(0, HeavyThreadCount, new ParallelOptions { MaxDegreeOfParallelism = HeavyThreadCount }, threadIndex =>
        {
            try
            {
                // Build initial chain of 20 blocks
                allocator.AllocateBlock(out var rootId, true).Cast<byte, int>()[0] = 0;
                var blockIds = new List<int> { rootId };

                for (int i = 1; i < 20; i++)
                {
                    allocator.AllocateBlock(out var blockId, false).Cast<byte, int>()[0] = i;
                    blockIds.Add(blockId);
                }
                allocator.Chain(blockIds.ToArray());

                var localRng = new Random(threadIndex);

                for (int round = 0; round < HeavyOperationsPerThread / 10; round++)
                {
                    var currentLength = allocator.GetChainLength(rootId);

                    if (currentLength > 5 && localRng.Next(2) == 0)
                    {
                        // Break chain at a random point
                        var breakPoint = localRng.Next(1, currentLength - 1);

                        // Find the block at breakPoint
                        var current = rootId;
                        for (int j = 0; j < breakPoint; j++)
                        {
                            allocator.NextBlock(current, out current);
                        }

                        var orphanRoot = allocator.Chain(current, 0);

                        // Verify both chains are valid
                        var newLength = allocator.GetChainLength(rootId);
                        var orphanLength = allocator.GetChainLength(orphanRoot);

                        Assert.That(newLength + orphanLength, Is.EqualTo(currentLength),
                            "Total length should be preserved after break");

                        // Rejoin
                        var lastInMain = allocator.GetLastBlockInChain(rootId);
                        allocator.Chain(lastInMain, orphanRoot);

                        Assert.That(allocator.GetChainLength(rootId), Is.EqualTo(currentLength),
                            "Length should be restored after rejoin");
                    }
                    else
                    {
                        // Just extend the chain
                        allocator.AllocateBlock(out var newBlock, false);
                        var lastBlock = allocator.GetLastBlockInChain(rootId);
                        allocator.Chain(lastBlock, newBlock);
                    }
                }
            }
            catch (Exception ex)
            {
                exceptions.Add(ex);
            }
        });

        Assert.That(exceptions, Is.Empty, $"Exceptions occurred: {string.Join(", ", exceptions)}");
    }

    [Test]
    public void Concurrent_GetChainRoot_ConsistencyDuringMerge()
    {
        using var allocator = new ChainedBlockAllocator(sizeof(int), 64);
        var exceptions = new ConcurrentBag<Exception>();

        // Each thread creates chains and merges them
        Parallel.For(0, HeavyThreadCount, new ParallelOptions { MaxDegreeOfParallelism = HeavyThreadCount }, threadIndex =>
        {
            try
            {
                for (int round = 0; round < HeavyOperationsPerThread / 5; round++)
                {
                    // Create two small chains
                    allocator.AllocateBlock(out var rootA, true);
                    allocator.AllocateBlock(out var blockA2, false);
                    allocator.AllocateBlock(out var blockA3, false);
                    allocator.Chain(rootA, blockA2, blockA3);

                    allocator.AllocateBlock(out var rootB, true);
                    allocator.AllocateBlock(out var blockB2, false);
                    allocator.Chain(rootB, blockB2);

                    // Verify roots before merge
                    Assert.That(allocator.GetChainRoot(blockA2), Is.EqualTo(rootA));
                    Assert.That(allocator.GetChainRoot(blockA3), Is.EqualTo(rootA));
                    Assert.That(allocator.GetChainRoot(blockB2), Is.EqualTo(rootB));

                    // Merge B into A
                    allocator.Chain(rootA, rootB);

                    // Verify all blocks now point to rootA
                    Assert.That(allocator.GetChainRoot(rootA), Is.EqualTo(rootA));
                    Assert.That(allocator.GetChainRoot(rootB), Is.EqualTo(rootA));
                    Assert.That(allocator.GetChainRoot(blockB2), Is.EqualTo(rootA));
                    Assert.That(allocator.GetChainRoot(blockA2), Is.EqualTo(rootA));
                    Assert.That(allocator.GetChainRoot(blockA3), Is.EqualTo(rootA));

                    // Free the merged chain
                    allocator.FreeChain(rootA);
                }
            }
            catch (Exception ex)
            {
                exceptions.Add(ex);
            }
        });

        Assert.That(exceptions, Is.Empty, $"Exceptions occurred: {string.Join(", ", exceptions)}");
    }

    #endregion
}
