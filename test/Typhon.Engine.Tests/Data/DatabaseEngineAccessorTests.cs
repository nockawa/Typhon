using Microsoft.Extensions.DependencyInjection;
using NUnit.Framework;
using System.Linq;

namespace Typhon.Engine.Tests;

class DatabaseEngineAccessorTests : TestBase<DatabaseEngineAccessorTests>
{
    [Test]
    public void GetAllComponentTables_ExposesRegisteredComponents()
    {
        using var dbe = ServiceProvider.GetRequiredService<DatabaseEngine>();
        RegisterComponents(dbe);

        var tables = dbe.GetAllComponentTables().ToArray();

        Assert.That(tables.Length, Is.GreaterThanOrEqualTo(6), "RegisterComponents registers 6 test components");

        var names = tables.Select(t => t.Definition.Name).ToArray();
        Assert.That(names, Does.Contain("Typhon.Schema.UnitTest.CompA"));
        Assert.That(names, Does.Contain("Typhon.Schema.UnitTest.CompB"));
        Assert.That(names, Does.Contain("Typhon.Schema.UnitTest.CompC"));
    }
}
