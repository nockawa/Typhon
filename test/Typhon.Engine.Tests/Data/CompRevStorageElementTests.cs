using NUnit.Framework;
using System.Runtime.CompilerServices;

namespace Typhon.Engine.Tests;

class CompRevStorageElementTests
{
    [Test]
    public unsafe void Sizeof_Is12Bytes()
    {
        Assert.That(sizeof(CompRevStorageElement), Is.EqualTo(12));
    }

    [Test]
    public void TSN_FullPrecision_RoundTrips()
    {
        var el = new CompRevStorageElement();

        // Verify full 48-bit TSN precision (no bit 0 masking)
        el.TSN = 1;
        Assert.That(el.TSN, Is.EqualTo(1), "Odd TSN should be stored with full precision");

        el.TSN = 3;
        Assert.That(el.TSN, Is.EqualTo(3));

        el.TSN = 0xFFFF;
        Assert.That(el.TSN, Is.EqualTo(0xFFFF), "All lower 16 bits should round-trip");

        el.TSN = (1L << 47) - 1;
        Assert.That(el.TSN, Is.EqualTo((1L << 47) - 1), "Max 48-bit TSN should round-trip");

        el.TSN = 0x123456789ABC;
        Assert.That(el.TSN, Is.EqualTo(0x123456789ABC), "Arbitrary 48-bit TSN should round-trip");
    }

    [Test]
    public void IsolationFlag_IndependentOfTSN()
    {
        var el = new CompRevStorageElement();

        // Set TSN, then IsolationFlag — TSN should not change
        el.TSN = 12345;
        el.IsolationFlag = true;
        Assert.That(el.TSN, Is.EqualTo(12345), "Setting IsolationFlag should not alter TSN");
        Assert.That(el.IsolationFlag, Is.True);

        el.IsolationFlag = false;
        Assert.That(el.TSN, Is.EqualTo(12345), "Clearing IsolationFlag should not alter TSN");
        Assert.That(el.IsolationFlag, Is.False);

        // Set IsolationFlag, then TSN — IsolationFlag should not change
        el.IsolationFlag = true;
        el.TSN = 99999;
        Assert.That(el.IsolationFlag, Is.True, "Setting TSN should not alter IsolationFlag");
        Assert.That(el.TSN, Is.EqualTo(99999));
    }

    [Test]
    public void UowId_RoundTrips()
    {
        var el = new CompRevStorageElement();

        el.UowId = 0;
        Assert.That(el.UowId, Is.EqualTo(0));

        el.UowId = 1;
        Assert.That(el.UowId, Is.EqualTo(1));

        el.UowId = 32767; // max 15-bit value
        Assert.That(el.UowId, Is.EqualTo(32767));
    }

    [Test]
    public void UowId_IndependentOfIsolationFlag()
    {
        var el = new CompRevStorageElement();

        // Set UowId, then IsolationFlag — UowId should not change
        el.UowId = 12345;
        el.IsolationFlag = true;
        Assert.That(el.UowId, Is.EqualTo(12345), "Setting IsolationFlag should not alter UowId");

        el.IsolationFlag = false;
        Assert.That(el.UowId, Is.EqualTo(12345), "Clearing IsolationFlag should not alter UowId");

        // Set IsolationFlag, then UowId — IsolationFlag should not change
        el.IsolationFlag = true;
        el.UowId = 9999;
        Assert.That(el.IsolationFlag, Is.True, "Setting UowId should not alter IsolationFlag");
        Assert.That(el.UowId, Is.EqualTo(9999));
    }

    [Test]
    public void UowId_IndependentOfTSN()
    {
        var el = new CompRevStorageElement();

        el.UowId = 500;
        el.TSN = 0xABCDEF;
        Assert.That(el.UowId, Is.EqualTo(500), "Setting TSN should not alter UowId");

        el.TSN = 12345;
        el.UowId = 700;
        Assert.That(el.TSN, Is.EqualTo(12345), "Setting UowId should not alter TSN");
    }

    [Test]
    public void Void_ClearsAllFields()
    {
        var el = new CompRevStorageElement();
        el.ComponentChunkId = 42;
        el.TSN = 99999;
        el.IsolationFlag = true;
        el.UowId = 100;

        el.Void();

        Assert.That(el.ComponentChunkId, Is.EqualTo(0));
        Assert.That(el.TSN, Is.EqualTo(0));
        Assert.That(el.IsolationFlag, Is.False);
        Assert.That(el.UowId, Is.EqualTo(0));
        Assert.That(el.IsVoid, Is.True);
    }

    [Test]
    public void IsVoid_FalseWhenAnyFieldSet()
    {
        var el = new CompRevStorageElement();
        Assert.That(el.IsVoid, Is.True, "Default-initialized element should be void");

        el.ComponentChunkId = 1;
        Assert.That(el.IsVoid, Is.False);
        el.Void();

        el.TSN = 1;
        Assert.That(el.IsVoid, Is.False);
        el.Void();

        el.IsolationFlag = true;
        Assert.That(el.IsVoid, Is.False);
        el.Void();

        el.UowId = 1;
        Assert.That(el.IsVoid, Is.False);
    }

    [Test]
    public unsafe void ChunkCapacity_Root_Is3()
    {
        // Root chunk: (64 - sizeof(CompRevStorageHeader)) / sizeof(CompRevStorageElement) = (64-20)/12 = 3
        Assert.That(ComponentRevisionManager.CompRevCountInRoot, Is.EqualTo(3));
    }

    [Test]
    public unsafe void ChunkCapacity_Overflow_Is5()
    {
        // Overflow chunk: 64 / sizeof(CompRevStorageElement) = 64/12 = 5
        Assert.That(ComponentRevisionManager.CompRevCountInNext, Is.EqualTo(5));
    }

    [Test]
    public void AllFields_Simultaneous_Orthogonal()
    {
        var el = new CompRevStorageElement();

        // Set all fields to distinct values
        el.ComponentChunkId = 42;
        el.TSN = 0x1234ABCD5678;
        el.IsolationFlag = true;
        el.UowId = 31000;

        // Verify all are independent
        Assert.That(el.ComponentChunkId, Is.EqualTo(42));
        Assert.That(el.TSN, Is.EqualTo(0x1234ABCD5678));
        Assert.That(el.IsolationFlag, Is.True);
        Assert.That(el.UowId, Is.EqualTo(31000));
        Assert.That(el.IsVoid, Is.False);
    }
}
