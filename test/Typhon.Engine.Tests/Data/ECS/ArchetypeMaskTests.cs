using NUnit.Framework;

namespace Typhon.Engine.Tests;

unsafe class ArchetypeMaskTests
{
    // ═══════════════════════════════════════════════════════════════════════
    // ArchetypeMask256
    // ═══════════════════════════════════════════════════════════════════════

    [Test]
    public void Mask256_SizeOf_32Bytes()
    {
        Assert.That(sizeof(ArchetypeMask256), Is.EqualTo(32));
    }

    [Test]
    public void Mask256_SetAndTest_SingleBit()
    {
        var mask = new ArchetypeMask256();
        mask.Set(42);
        Assert.That(mask.Test(42), Is.True);
        Assert.That(mask.Test(41), Is.False);
        Assert.That(mask.Test(43), Is.False);
    }

    [Test]
    public void Mask256_SetAndTest_Bit0()
    {
        var mask = new ArchetypeMask256();
        mask.Set(0);
        Assert.That(mask.Test(0), Is.True);
        Assert.That(mask.Test(1), Is.False);
    }

    [Test]
    public void Mask256_SetAndTest_Bit63_WordBoundary()
    {
        var mask = new ArchetypeMask256();
        mask.Set(63);
        Assert.That(mask.Test(63), Is.True);
        Assert.That(mask.Test(62), Is.False);
        Assert.That(mask.Test(64), Is.False);
    }

    [Test]
    public void Mask256_SetAndTest_Bit64_CrossesWordBoundary()
    {
        var mask = new ArchetypeMask256();
        mask.Set(64);
        Assert.That(mask.Test(64), Is.True);
        Assert.That(mask.Test(63), Is.False);
        Assert.That(mask.Test(65), Is.False);
    }

    [Test]
    public void Mask256_SetAndTest_Bit255_MaxBit()
    {
        var mask = new ArchetypeMask256();
        mask.Set(255);
        Assert.That(mask.Test(255), Is.True);
        Assert.That(mask.Test(254), Is.False);
    }

    [Test]
    public void Mask256_Clear()
    {
        var mask = new ArchetypeMask256();
        mask.Set(10);
        mask.Set(20);
        mask.Clear(10);
        Assert.That(mask.Test(10), Is.False);
        Assert.That(mask.Test(20), Is.True);
    }

    [Test]
    public void Mask256_And_Intersection()
    {
        var a = new ArchetypeMask256();
        a.Set(1); a.Set(2); a.Set(3);

        var b = new ArchetypeMask256();
        b.Set(2); b.Set(3); b.Set(4);

        var result = a.And(in b);
        Assert.That(result.Test(1), Is.False);
        Assert.That(result.Test(2), Is.True);
        Assert.That(result.Test(3), Is.True);
        Assert.That(result.Test(4), Is.False);
    }

    [Test]
    public void Mask256_AndNot_Exclusion()
    {
        var a = new ArchetypeMask256();
        a.Set(1); a.Set(2); a.Set(3);

        var b = new ArchetypeMask256();
        b.Set(2);

        var result = a.AndNot(in b);
        Assert.That(result.Test(1), Is.True);
        Assert.That(result.Test(2), Is.False);
        Assert.That(result.Test(3), Is.True);
    }

    [Test]
    public void Mask256_Or_Union()
    {
        var a = ArchetypeMask256.FromArchetype(1);
        var b = ArchetypeMask256.FromArchetype(100);
        var result = a.Or(in b);
        Assert.That(result.Test(1), Is.True);
        Assert.That(result.Test(100), Is.True);
        Assert.That(result.PopCount, Is.EqualTo(2));
    }

    [Test]
    public void Mask256_IsEmpty_Default()
    {
        var mask = new ArchetypeMask256();
        Assert.That(mask.IsEmpty, Is.True);
    }

    [Test]
    public void Mask256_IsEmpty_AfterSet()
    {
        var mask = new ArchetypeMask256();
        mask.Set(0);
        Assert.That(mask.IsEmpty, Is.False);
    }

    [Test]
    public void Mask256_PopCount()
    {
        var mask = new ArchetypeMask256();
        Assert.That(mask.PopCount, Is.EqualTo(0));

        mask.Set(0); mask.Set(63); mask.Set(64); mask.Set(255);
        Assert.That(mask.PopCount, Is.EqualTo(4));
    }

    [Test]
    public void Mask256_FromSubtree()
    {
        var ids = new ushort[] { 1, 5, 10, 200 };
        var mask = ArchetypeMask256.FromSubtree(ids);

        Assert.That(mask.Test(1), Is.True);
        Assert.That(mask.Test(5), Is.True);
        Assert.That(mask.Test(10), Is.True);
        Assert.That(mask.Test(200), Is.True);
        Assert.That(mask.Test(2), Is.False);
        Assert.That(mask.PopCount, Is.EqualTo(4));
    }

    // ═══════════════════════════════════════════════════════════════════════
    // ArchetypeMaskLarge
    // ═══════════════════════════════════════════════════════════════════════

    [Test]
    public void MaskLarge_SetAndTest()
    {
        var mask = new ArchetypeMaskLarge(4095);
        mask.Set(300);
        Assert.That(mask.Test(300), Is.True);
        Assert.That(mask.Test(299), Is.False);
    }

    [Test]
    public void MaskLarge_SetAndTest_MaxId()
    {
        var mask = new ArchetypeMaskLarge(4095);
        mask.Set(4095);
        Assert.That(mask.Test(4095), Is.True);
    }

    [Test]
    public void MaskLarge_And()
    {
        var a = new ArchetypeMaskLarge(500);
        a.Set(300); a.Set(400);

        var b = new ArchetypeMaskLarge(500);
        b.Set(300); b.Set(500);

        var result = a.And(in b);
        Assert.That(result.Test(300), Is.True);
        Assert.That(result.Test(400), Is.False);
        Assert.That(result.Test(500), Is.False);
    }

    [Test]
    public void MaskLarge_PopCount()
    {
        var mask = new ArchetypeMaskLarge(1000);
        mask.Set(100); mask.Set(500); mask.Set(999);
        Assert.That(mask.PopCount, Is.EqualTo(3));
    }

    [Test]
    public void MaskLarge_IsEmpty()
    {
        var mask = new ArchetypeMaskLarge(1000);
        Assert.That(mask.IsEmpty, Is.True);
        mask.Set(500);
        Assert.That(mask.IsEmpty, Is.False);
    }
}
