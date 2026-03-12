using Microsoft.Extensions.DependencyInjection;
using NUnit.Framework;

namespace Typhon.Engine.Tests;

class IndexRefTests : TestBase<IndexRefTests>
{
    [Test]
    public void GetPKIndexRef_ReturnsValid()
    {
        using var dbe = ServiceProvider.GetRequiredService<DatabaseEngine>();
        RegisterComponents(dbe);

        var indexRef = dbe.GetPKIndexRef<CompA>();

        Assert.That(indexRef.IsPrimaryKey, Is.True);
        Assert.That(indexRef.FieldIndex, Is.EqualTo(-1));
        indexRef.Validate(); // should not throw
    }

    [Test]
    public void GetIndexRef_SecondaryField_ReturnsCorrectIndex()
    {
        using var dbe = ServiceProvider.GetRequiredService<DatabaseEngine>();
        RegisterComponents(dbe);

        // CompD.B is a unique secondary index
        var indexRef = dbe.GetIndexRef<CompD, int>(d => d.B);

        Assert.That(indexRef.IsPrimaryKey, Is.False);
        Assert.That(indexRef.FieldIndex, Is.GreaterThanOrEqualTo(0));
        indexRef.Validate(); // should not throw
    }

    [Test]
    public void GetIndexRef_AllowMultipleField_ReturnsCorrectIndex()
    {
        using var dbe = ServiceProvider.GetRequiredService<DatabaseEngine>();
        RegisterComponents(dbe);

        // CompD.A is an AllowMultiple secondary index
        var indexRef = dbe.GetIndexRef<CompD, float>(d => d.A);

        Assert.That(indexRef.IsPrimaryKey, Is.False);
        Assert.That(indexRef.FieldIndex, Is.GreaterThanOrEqualTo(0));
        indexRef.Validate();
    }

    [Test]
    public void GetIndexRef_NonIndexedField_Throws()
    {
        using var dbe = ServiceProvider.GetRequiredService<DatabaseEngine>();
        RegisterComponents(dbe);

        // CompA.A has no [Index] attribute
        Assert.Throws<System.InvalidOperationException>(() =>
            dbe.GetIndexRef<CompA, int>(a => a.A));
    }

    [Test]
    public void GetIndexRef_UnregisteredType_Throws()
    {
        using var dbe = ServiceProvider.GetRequiredService<DatabaseEngine>();
        // Don't register any components

        Assert.Throws<System.InvalidOperationException>(() =>
            dbe.GetPKIndexRef<CompA>());
    }

    [Test]
    public void Validate_StaleRef_Throws()
    {
        using var dbe = ServiceProvider.GetRequiredService<DatabaseEngine>();
        RegisterComponents(dbe);

        var indexRef = dbe.GetPKIndexRef<CompA>();
        indexRef.Validate(); // valid now

        // Simulate a layout version bump by getting the ComponentTable and incrementing its layout version
        // via a second call to BuildIndexedFieldInfo (triggered by re-registration or schema migration).
        // Since we can't easily trigger that, we construct a stale IndexRef manually.
        var ct = dbe.GetComponentTable<CompA>();
        var staleRef = new IndexRef(-1, ct, ct.IndexLayoutVersion - 1);

        Assert.Throws<System.InvalidOperationException>(() => staleRef.Validate());
    }

    [Test]
    public void Validate_DefaultIndexRef_Throws()
    {
        var defaultRef = default(IndexRef);
        Assert.Throws<System.InvalidOperationException>(() => defaultRef.Validate());
    }
}
