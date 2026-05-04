using NUnit.Framework;
using Typhon.Workbench.Hosting;

namespace Typhon.Workbench.Tests;

/// <summary>
/// Unit tests for <see cref="EditorLauncher"/> that don't actually spawn editors. We only test
/// the dispatch logic + URL construction. Real cross-platform launch verification (does VS Code
/// actually open?) is a manual QA step on Windows + macOS.
/// </summary>
[TestFixture]
public sealed class EditorLauncherTests
{
    [Test]
    public void BuildFileUrl_VsCode_ProducesExpectedFormat()
    {
        var url = EditorLauncher.BuildFileUrl("vscode", "C:/Dev/Typhon/src/BTree.cs", 217);
        Assert.That(url, Is.EqualTo("vscode://file/C%3A/Dev/Typhon/src/BTree.cs:217"));
    }

    [Test]
    public void BuildFileUrl_Cursor_PreservesScheme()
    {
        var url = EditorLauncher.BuildFileUrl("cursor", "/repo/file.cs", 42);
        Assert.That(url, Does.StartWith("cursor://file/"));
        Assert.That(url, Does.EndWith(":42"));
    }

    [Test]
    public void BuildFileUrl_NormalizesBackslashesToForwardSlashes()
    {
        var url = EditorLauncher.BuildFileUrl("vscode", @"C:\Dev\Typhon\file.cs", 1);
        Assert.That(url, Does.Not.Contain("\\"));
        Assert.That(url, Does.Contain("/Dev/Typhon/file.cs"));
    }

    [Test]
    public void Launch_WithEmptyPath_ReturnsError()
    {
        var launcher = new EditorLauncher();
        var opts = new EditorOptions { Kind = EditorKind.VsCode };
        var result = launcher.Launch(opts, "", line: 1, column: null);
        Assert.That(result.Success, Is.False);
        Assert.That(result.ErrorMessage, Does.Contain("file path"));
    }

    [Test]
    public void Launch_CustomKindWithEmptyTemplate_ReturnsError()
    {
        var launcher = new EditorLauncher();
        var opts = new EditorOptions { Kind = EditorKind.Custom, CustomCommand = "" };
        var result = launcher.Launch(opts, "/tmp/file.cs", line: 1, column: null);
        Assert.That(result.Success, Is.False);
        Assert.That(result.ErrorMessage, Does.Contain("Custom command"));
        Assert.That(result.Hint, Does.Contain("Options"));
    }

    [Test]
    public void Launch_VisualStudioOnNonWindows_ReturnsError()
    {
        if (System.Runtime.InteropServices.RuntimeInformation.IsOSPlatform(System.Runtime.InteropServices.OSPlatform.Windows))
        {
            Assert.Ignore("Test only runs on non-Windows OSes.");
            return;
        }
        var launcher = new EditorLauncher();
        var opts = new EditorOptions { Kind = EditorKind.VisualStudio };
        var result = launcher.Launch(opts, "/tmp/file.cs", line: 1, column: null);
        Assert.That(result.Success, Is.False);
        Assert.That(result.ErrorMessage, Does.Contain("Visual Studio"));
    }
}
