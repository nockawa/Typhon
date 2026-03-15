using NUnit.Framework;

namespace Typhon.Engine.Tests;

class EntityIdTests
{
    [Test]
    public void Constructor_PacksEntityKeyAndArchetypeId()
    {
        var id = new EntityId(42, 7);
        Assert.That(id.EntityKey, Is.EqualTo(42));
        Assert.That(id.ArchetypeId, Is.EqualTo(7));
    }

    [Test]
    public void Constructor_MaxEntityKey_52Bit()
    {
        long maxKey = (1L << 52) - 1; // 4,503,599,627,370,495
        var id = new EntityId(maxKey, 0);
        Assert.That(id.EntityKey, Is.EqualTo(maxKey));
        Assert.That(id.ArchetypeId, Is.EqualTo(0));
    }

    [Test]
    public void Constructor_MaxArchetypeId_4095()
    {
        var id = new EntityId(1, 4095);
        Assert.That(id.EntityKey, Is.EqualTo(1));
        Assert.That(id.ArchetypeId, Is.EqualTo(4095));
    }

    [Test]
    public void Constructor_ArchetypeIdBitsMasked()
    {
        // Only lower 12 bits should be used
        var id = new EntityId(100, 0xABC);
        Assert.That(id.ArchetypeId, Is.EqualTo(0xABC));
    }

    [Test]
    public void Null_IsDefault()
    {
        Assert.That(EntityId.Null.IsNull, Is.True);
        Assert.That(EntityId.Null.EntityKey, Is.EqualTo(0));
        Assert.That(EntityId.Null.ArchetypeId, Is.EqualTo(0));
    }

    [Test]
    public void IsNull_NonDefault_ReturnsFalse()
    {
        var id = new EntityId(1, 1);
        Assert.That(id.IsNull, Is.False);
    }

    [Test]
    public void Equality_SameValues_Equal()
    {
        var a = new EntityId(42, 7);
        var b = new EntityId(42, 7);
        Assert.That(a, Is.EqualTo(b));
        Assert.That(a == b, Is.True);
        Assert.That(a != b, Is.False);
    }

    [Test]
    public void Equality_DifferentKey_NotEqual()
    {
        var a = new EntityId(1, 7);
        var b = new EntityId(2, 7);
        Assert.That(a, Is.Not.EqualTo(b));
        Assert.That(a != b, Is.True);
    }

    [Test]
    public void Equality_DifferentArchetype_NotEqual()
    {
        var a = new EntityId(42, 1);
        var b = new EntityId(42, 2);
        Assert.That(a, Is.Not.EqualTo(b));
    }

    [Test]
    public void GetHashCode_SameValues_SameHash()
    {
        var a = new EntityId(42, 7);
        var b = new EntityId(42, 7);
        Assert.That(a.GetHashCode(), Is.EqualTo(b.GetHashCode()));
    }

    [Test]
    public unsafe void SizeOf_8Bytes()
    {
        Assert.That(sizeof(EntityId), Is.EqualTo(8));
    }

    [Test]
    public void ToString_Null_ShowsNull()
    {
        Assert.That(EntityId.Null.ToString(), Is.EqualTo("Entity(Null)"));
    }

    [Test]
    public void ToString_NonNull_ShowsKeyAndArch()
    {
        var id = new EntityId(42, 7);
        Assert.That(id.ToString(), Does.Contain("42").And.Contain("7"));
    }

    [Test]
    public void RoundTrip_RawValue_Preserves()
    {
        var id = new EntityId(123456789L, 2048);
        var raw = id.RawValue;
        Assert.That(raw, Is.Not.EqualTo(0UL));

        // Reconstruct
        var id2 = new EntityId((long)(raw >> 12), (ushort)(raw & 0xFFF));
        Assert.That(id2.EntityKey, Is.EqualTo(id.EntityKey));
        Assert.That(id2.ArchetypeId, Is.EqualTo(id.ArchetypeId));
    }
}
