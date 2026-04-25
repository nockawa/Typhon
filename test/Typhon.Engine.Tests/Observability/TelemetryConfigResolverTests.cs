using Microsoft.Extensions.Configuration;
using NUnit.Framework;
using System.Collections.Generic;

namespace Typhon.Engine.Tests.Observability;

/// <summary>
/// Unit tests for <see cref="TelemetryConfigResolver"/> — covers the three semantic cases of the
/// parent-implies-children inheritance formula.
/// </summary>
[TestFixture]
public class TelemetryConfigResolverTests
{
    private static readonly Node TestTree = new("Concurrency",
    [
        new Node("AccessControl",
        [
            new Node("SharedAcquire"),
        ]),
    ]);

    [Test]
    public void ParentOff_DisablesChildrenEvenIfExplicitlyTrue()
    {
        var config = Build(new()
        {
            ["Typhon:Profiler:Concurrency:AccessControl:Enabled"] = "true",
            ["Typhon:Profiler:Concurrency:AccessControl:SharedAcquire:Enabled"] = "true",
        });

        var map = TelemetryConfigResolver.Resolve(
            TestTree, rootEffective: false, config, "Typhon:Profiler");

        Assert.Multiple(() =>
        {
            Assert.That(map["Concurrency"], Is.False, "Root effective is false → root entry false.");
            Assert.That(map["Concurrency:AccessControl"], Is.False, "Parent off → child off, even with explicit Enabled = true.");
            Assert.That(map["Concurrency:AccessControl:SharedAcquire"], Is.False, "Grandparent off → grandchild off.");
        });
    }

    [Test]
    public void ParentOn_LeafExplicitOff_LeafIsOff()
    {
        var config = Build(new()
        {
            ["Typhon:Profiler:Concurrency:AccessControl:SharedAcquire:Enabled"] = "false",
        });

        var map = TelemetryConfigResolver.Resolve(
            TestTree, rootEffective: true, config, "Typhon:Profiler");

        Assert.Multiple(() =>
        {
            Assert.That(map["Concurrency"], Is.True, "Root effective true.");
            Assert.That(map["Concurrency:AccessControl"], Is.True, "Inherits true from root (no explicit key).");
            Assert.That(map["Concurrency:AccessControl:SharedAcquire"], Is.False, "Explicit override wins.");
        });
    }

    [Test]
    public void ParentOn_LeafImplicit_LeafInheritsTrue()
    {
        var config = Build(new());  // no leaf keys at all

        var map = TelemetryConfigResolver.Resolve(
            TestTree, rootEffective: true, config, "Typhon:Profiler");

        Assert.Multiple(() =>
        {
            Assert.That(map["Concurrency"], Is.True);
            Assert.That(map["Concurrency:AccessControl"], Is.True, "Implicit → inherits parent.");
            Assert.That(map["Concurrency:AccessControl:SharedAcquire"], Is.True, "Implicit at every level.");
        });
    }

    private static IConfiguration Build(Dictionary<string, string> kv) =>
        new ConfigurationBuilder().AddInMemoryCollection(kv).Build();
}
