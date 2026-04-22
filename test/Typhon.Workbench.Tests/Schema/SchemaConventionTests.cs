using NUnit.Framework;
using Typhon.Workbench.Schema;

namespace Typhon.Workbench.Tests.Schema;

[TestFixture]
public sealed class SchemaConventionTests
{
    private string _tempDir;

    [SetUp]
    public void SetUp()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "typhon-wb-conv-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
    }

    [TearDown]
    public void TearDown()
    {
        try { if (Directory.Exists(_tempDir)) Directory.Delete(_tempDir, recursive: true); }
        catch { /* best-effort */ }
    }

    [Test]
    public void Resolve_EmptyDir_ReturnsEmpty()
    {
        var typhon = Path.Combine(_tempDir, "db.typhon");
        File.WriteAllText(typhon, "");
        var result = SchemaConvention.ResolveConventionally(typhon);
        Assert.That(result, Is.Empty);
    }

    [Test]
    public void Resolve_SingleSchemaDll_ReturnsIt()
    {
        var typhon = Path.Combine(_tempDir, "db.typhon");
        var dll = Path.Combine(_tempDir, "Game.schema.dll");
        File.WriteAllText(typhon, "");
        File.WriteAllText(dll, "");
        var result = SchemaConvention.ResolveConventionally(typhon);
        Assert.That(result, Has.Length.EqualTo(1));
        Assert.That(result[0], Is.EqualTo(dll).IgnoreCase);
    }

    [Test]
    public void Resolve_MultipleSchemaDlls_ReturnsAllSortedAlpha()
    {
        var typhon = Path.Combine(_tempDir, "db.typhon");
        var a = Path.Combine(_tempDir, "B.schema.dll");
        var b = Path.Combine(_tempDir, "A.schema.dll");
        File.WriteAllText(typhon, "");
        File.WriteAllText(a, "");
        File.WriteAllText(b, "");
        var result = SchemaConvention.ResolveConventionally(typhon);
        Assert.That(result, Has.Length.EqualTo(2));
        Assert.That(result[0], Does.EndWith("A.schema.dll"));
        Assert.That(result[1], Does.EndWith("B.schema.dll"));
    }

    [Test]
    public void Resolve_RegularDllNotMatched()
    {
        var typhon = Path.Combine(_tempDir, "db.typhon");
        var dll = Path.Combine(_tempDir, "Helper.dll");
        File.WriteAllText(typhon, "");
        File.WriteAllText(dll, "");
        var result = SchemaConvention.ResolveConventionally(typhon);
        Assert.That(result, Is.Empty);
    }

    [Test]
    public void Resolve_NestedSubfoldersIgnored()
    {
        var typhon = Path.Combine(_tempDir, "db.typhon");
        Directory.CreateDirectory(Path.Combine(_tempDir, "sub"));
        File.WriteAllText(typhon, "");
        File.WriteAllText(Path.Combine(_tempDir, "sub", "Nested.schema.dll"), "");
        var result = SchemaConvention.ResolveConventionally(typhon);
        Assert.That(result, Is.Empty);
    }
}
