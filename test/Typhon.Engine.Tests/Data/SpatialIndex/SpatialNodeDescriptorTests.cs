using System.Collections.Generic;
using NUnit.Framework;

namespace Typhon.Engine.Tests;

[TestFixture]
public class SpatialNodeDescriptorTests
{
    private static IEnumerable<TestCaseData> AllVariants()
    {
        yield return new TestCaseData(SpatialNodeDescriptor.R2Df32).SetName("R2Df32");
        yield return new TestCaseData(SpatialNodeDescriptor.R3Df32).SetName("R3Df32");
        yield return new TestCaseData(SpatialNodeDescriptor.R2Df64).SetName("R2Df64");
        yield return new TestCaseData(SpatialNodeDescriptor.R3Df64).SetName("R3Df64");
    }

    [Test]
    public void KnownCapacities_MatchDesignDoc()
    {
        // Corrected table from 02-node-layout.md lines 136-141
        Assert.That(SpatialNodeDescriptor.R2Df32.LeafCapacity, Is.EqualTo(20), "R2Df32 LeafCap");
        Assert.That(SpatialNodeDescriptor.R2Df32.InternalCapacity, Is.EqualTo(24), "R2Df32 InternalCap");
        Assert.That(SpatialNodeDescriptor.R2Df32.MinFill, Is.EqualTo(8), "R2Df32 MinFill");

        Assert.That(SpatialNodeDescriptor.R3Df32.LeafCapacity, Is.EqualTo(14), "R3Df32 LeafCap");
        Assert.That(SpatialNodeDescriptor.R3Df32.InternalCapacity, Is.EqualTo(17), "R3Df32 InternalCap");
        Assert.That(SpatialNodeDescriptor.R3Df32.MinFill, Is.EqualTo(6), "R3Df32 MinFill");

        Assert.That(SpatialNodeDescriptor.R2Df64.LeafCapacity, Is.EqualTo(11), "R2Df64 LeafCap");
        Assert.That(SpatialNodeDescriptor.R2Df64.InternalCapacity, Is.EqualTo(12), "R2Df64 InternalCap");
        Assert.That(SpatialNodeDescriptor.R2Df64.MinFill, Is.EqualTo(5), "R2Df64 MinFill");

        Assert.That(SpatialNodeDescriptor.R3Df64.LeafCapacity, Is.EqualTo(12), "R3Df64 LeafCap");
        Assert.That(SpatialNodeDescriptor.R3Df64.InternalCapacity, Is.EqualTo(13), "R3Df64 InternalCap");
        Assert.That(SpatialNodeDescriptor.R3Df64.MinFill, Is.EqualTo(5), "R3Df64 MinFill");
    }

    [Test]
    public void HeaderSizes_MatchDesignDoc()
    {
        Assert.That(SpatialNodeDescriptor.R2Df32.HeaderSize, Is.EqualTo(32), "R2Df32 header");
        Assert.That(SpatialNodeDescriptor.R3Df32.HeaderSize, Is.EqualTo(36), "R3Df32 header");
        Assert.That(SpatialNodeDescriptor.R2Df64.HeaderSize, Is.EqualTo(48), "R2Df64 header");
        Assert.That(SpatialNodeDescriptor.R3Df64.HeaderSize, Is.EqualTo(60), "R3Df64 header");
    }

    [Test]
    public void CoordParameters_MatchVariant()
    {
        Assert.That(SpatialNodeDescriptor.R2Df32.CoordCount, Is.EqualTo(4), "R2Df32 coords");
        Assert.That(SpatialNodeDescriptor.R2Df32.CoordSize, Is.EqualTo(4), "R2Df32 coord size");

        Assert.That(SpatialNodeDescriptor.R3Df32.CoordCount, Is.EqualTo(6), "R3Df32 coords");
        Assert.That(SpatialNodeDescriptor.R3Df32.CoordSize, Is.EqualTo(4), "R3Df32 coord size");

        Assert.That(SpatialNodeDescriptor.R2Df64.CoordCount, Is.EqualTo(4), "R2Df64 coords");
        Assert.That(SpatialNodeDescriptor.R2Df64.CoordSize, Is.EqualTo(8), "R2Df64 coord size");

        Assert.That(SpatialNodeDescriptor.R3Df64.CoordCount, Is.EqualTo(6), "R3Df64 coords");
        Assert.That(SpatialNodeDescriptor.R3Df64.CoordSize, Is.EqualTo(8), "R3Df64 coord size");
    }

    [Test]
    [TestCaseSource(nameof(AllVariants))]
    public void LeafLayout_FitsWithinStride(SpatialNodeDescriptor desc)
    {
        int leafEnd = desc.LeafIdOffset + desc.LeafCapacity * desc.LeafIdSize;
        Assert.That(leafEnd, Is.LessThanOrEqualTo(desc.Stride),
            $"Leaf data overflows stride: ends at {leafEnd}, stride is {desc.Stride}");
    }

    [Test]
    [TestCaseSource(nameof(AllVariants))]
    public void InternalLayout_FitsWithinStride(SpatialNodeDescriptor desc)
    {
        int internalEnd = desc.InternalIdOffset + desc.InternalCapacity * desc.InternalIdSize;
        Assert.That(internalEnd, Is.LessThanOrEqualTo(desc.Stride),
            $"Internal data overflows stride: ends at {internalEnd}, stride is {desc.Stride}");
    }

    [Test]
    [TestCaseSource(nameof(AllVariants))]
    public void Leaf_SOA_NoOverlap(SpatialNodeDescriptor desc)
    {
        // Each coord array: [LeafCoordOffsets + i * LeafCoordStride, + LeafCoordStride)
        // EntityId array: [LeafIdOffset, + LeafCapacity * 8)
        for (int i = 0; i < desc.CoordCount; i++)
        {
            int coordStart = desc.LeafCoordOffsets + i * desc.LeafCoordStride;
            int coordEnd = coordStart + desc.LeafCoordStride;

            // Coord arrays must not overlap with each other
            for (int j = i + 1; j < desc.CoordCount; j++)
            {
                int otherStart = desc.LeafCoordOffsets + j * desc.LeafCoordStride;
                Assert.That(coordEnd, Is.LessThanOrEqualTo(otherStart),
                    $"Leaf coord arrays {i} and {j} overlap");
            }

            // Coord arrays must not overlap with EntityId array
            Assert.That(coordEnd, Is.LessThanOrEqualTo(desc.LeafIdOffset),
                $"Leaf coord array {i} overlaps with EntityId array");
        }
    }

    [Test]
    [TestCaseSource(nameof(AllVariants))]
    public void Internal_SOA_NoOverlap(SpatialNodeDescriptor desc)
    {
        for (int i = 0; i < desc.CoordCount; i++)
        {
            int coordStart = desc.HeaderSize + i * desc.InternalCoordStride;
            int coordEnd = coordStart + desc.InternalCoordStride;

            for (int j = i + 1; j < desc.CoordCount; j++)
            {
                int otherStart = desc.HeaderSize + j * desc.InternalCoordStride;
                Assert.That(coordEnd, Is.LessThanOrEqualTo(otherStart),
                    $"Internal coord arrays {i} and {j} overlap");
            }

            Assert.That(coordEnd, Is.LessThanOrEqualTo(desc.InternalIdOffset),
                $"Internal coord array {i} overlaps with ChunkId array");
        }
    }

    [Test]
    [TestCaseSource(nameof(AllVariants))]
    public void EntryAreaSize_IsStride_MinusHeader(SpatialNodeDescriptor desc)
    {
        Assert.That(desc.EntryAreaSize, Is.EqualTo(desc.Stride - desc.HeaderSize));
    }

    [Test]
    public void ForVariant_ReturnsSameAsPrebuilt()
    {
        Assert.That(SpatialNodeDescriptor.ForVariant(SpatialVariant.R2Df32).LeafCapacity,
            Is.EqualTo(SpatialNodeDescriptor.R2Df32.LeafCapacity));
        Assert.That(SpatialNodeDescriptor.ForVariant(SpatialVariant.R3Df64).LeafCapacity,
            Is.EqualTo(SpatialNodeDescriptor.R3Df64.LeafCapacity));
    }
}
