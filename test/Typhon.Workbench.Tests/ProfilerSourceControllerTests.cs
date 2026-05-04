using System.Net;
using System.Net.Http.Json;
using NUnit.Framework;
using Typhon.Workbench.Hosting;

namespace Typhon.Workbench.Tests;

/// <summary>
/// Tests the /api/profiler/open-in-editor and /api/profiler/source endpoints. We don't actually
/// spawn editors here (covered by <see cref="EditorLauncherTests"/>) but exercise the controller's
/// validation, path-traversal guard, and source-window assembly.
/// </summary>
[TestFixture]
public sealed class ProfilerSourceControllerTests
{
    private WorkbenchFactory _factory;
    private HttpClient _client;
    private string _workspaceRoot;
    private string _testFile;

    [SetUp]
    public async Task SetUp()
    {
        _factory = new WorkbenchFactory();
        _client = _factory.CreateAuthenticatedClient();

        // Create a tiny C# file inside the per-test temp dir to act as the workspace root.
        _workspaceRoot = Path.Combine(_factory.DemoDirectory, "workspace");
        Directory.CreateDirectory(Path.Combine(_workspaceRoot, "src"));
        _testFile = Path.Combine(_workspaceRoot, "src", "Sample.cs");
        await File.WriteAllLinesAsync(_testFile, new[]
        {
            "namespace Sample;",                    // line 1
            "public class Demo",                    // line 2
            "{",                                     // line 3
            "    public void Method()",             // line 4
            "    {",                                 // line 5
            "        var span = Begin();",          // line 6
            "    }",                                 // line 7
            "}",                                     // line 8
        });

        // Configure the workspace root via the Options API.
        await _client.PatchAsJsonAsync("/api/options/profiler",
            new ProfilerOptions { WorkspaceRoot = _workspaceRoot });
    }

    [TearDown]
    public void TearDown() => _factory.Dispose();

    [Test]
    public async Task GetSource_ReturnsLineWindowAroundTarget()
    {
        var resp = await _client.GetAsync($"/api/profiler/source?path=/_/src/Sample.cs&line=4&context=2");
        Assert.That(resp.StatusCode, Is.EqualTo(HttpStatusCode.OK));

        var window = await resp.Content.ReadFromJsonAsync<SourceWindowDto>();
        Assert.That(window, Is.Not.Null);
        Assert.That(window!.Line, Is.EqualTo(4));
        Assert.That(window.StartLine, Is.EqualTo(2));
        Assert.That(window.EndLine, Is.EqualTo(6));
        Assert.That(window.Lines.Length, Is.EqualTo(5));
        Assert.That(window.Lines[2], Does.Contain("Method"));   // line 4 = index 2 in the window
    }

    [Test]
    public async Task GetSource_ClampsContextToFileBoundaries()
    {
        var resp = await _client.GetAsync($"/api/profiler/source?path=/_/src/Sample.cs&line=1&context=20");
        Assert.That(resp.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        var window = await resp.Content.ReadFromJsonAsync<SourceWindowDto>();
        Assert.That(window!.StartLine, Is.EqualTo(1));
        Assert.That(window.EndLine, Is.EqualTo(8)); // file has only 8 lines
    }

    [Test]
    public async Task GetSource_RejectsPathTraversal()
    {
        // Try to escape the workspace root.
        var resp = await _client.GetAsync($"/api/profiler/source?path=/_/../../../etc/passwd&line=1");
        Assert.That(resp.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest));
    }

    [Test]
    public async Task GetSource_NonExistentFile_Returns404()
    {
        var resp = await _client.GetAsync($"/api/profiler/source?path=/_/src/Missing.cs&line=1");
        Assert.That(resp.StatusCode, Is.EqualTo(HttpStatusCode.NotFound));
    }

    [Test]
    public async Task GetSource_LineZero_Returns400()
    {
        var resp = await _client.GetAsync($"/api/profiler/source?path=/_/src/Sample.cs&line=0");
        Assert.That(resp.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest));
    }

    [Test]
    public async Task OpenInEditor_NoFile_Returns400()
    {
        var resp = await _client.PostAsJsonAsync("/api/profiler/open-in-editor",
            new { File = "", Line = 1, Column = (int?)null });
        Assert.That(resp.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest));
    }

    [Test]
    public async Task OpenInEditor_WithCustomEmptyTemplate_ReturnsErrorPayload()
    {
        // Configure custom editor with empty template.
        await _client.PatchAsJsonAsync("/api/options/editor",
            new EditorOptions { Kind = EditorKind.Custom, CustomCommand = "" });

        var resp = await _client.PostAsJsonAsync("/api/profiler/open-in-editor",
            new { File = "/_/src/Sample.cs", Line = 4, Column = (int?)null });
        // The endpoint returns 200 with a structured Ok=false result so the client can show a toast.
        Assert.That(resp.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        var result = await resp.Content.ReadFromJsonAsync<OpenInEditorResultDto>();
        Assert.That(result!.Ok, Is.False);
        Assert.That(result.Error, Does.Contain("Custom"));
    }

    [Test]
    public void ResolveAbsolutePath_StripsRepoSentinel()
    {
        var abs = Typhon.Workbench.Controllers.ProfilerSourceController.ResolveAbsolutePath(
            "/_/src/Typhon.Engine/BTree.cs", "C:/Dev/Typhon");
        Assert.That(abs.Replace('\\', '/'), Does.EndWith("Dev/Typhon/src/Typhon.Engine/BTree.cs"));
    }

    [Test]
    public void AutoDetectRepoRoot_FindsAncestorWithGitDirectory()
    {
        // Build a temp tree: <root>/.git, <root>/sub/cwd. Set CWD to the deep folder, expect AutoDetect
        // to walk back up to <root>. Restore original CWD afterwards so we don't poison sibling tests.
        var tempRoot = Path.Combine(Path.GetTempPath(), $"adrt-{Guid.NewGuid():N}");
        var subCwd = Path.Combine(tempRoot, "sub", "cwd");
        Directory.CreateDirectory(subCwd);
        Directory.CreateDirectory(Path.Combine(tempRoot, ".git"));
        var savedCwd = Directory.GetCurrentDirectory();
        try
        {
            Directory.SetCurrentDirectory(subCwd);
            var detected = Typhon.Workbench.Controllers.ProfilerSourceController.AutoDetectRepoRoot();
            Assert.That(detected, Is.Not.Null);
            // Path.GetFullPath / DirectoryInfo round-trip may add a trailing separator on Windows.
            Assert.That(detected!.Replace('\\', '/').TrimEnd('/'),
                Is.EqualTo(tempRoot.Replace('\\', '/').TrimEnd('/')));
        }
        finally
        {
            Directory.SetCurrentDirectory(savedCwd);
            try { Directory.Delete(tempRoot, recursive: true); } catch { }
        }
    }

    [Test]
    public async Task GetWorkspaceRoot_ReturnsConfiguredOrAutoDetected()
    {
        var resp = await _client.GetAsync("/api/profiler/workspace-root");
        Assert.That(resp.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        var dto = await resp.Content.ReadFromJsonAsync<WorkspaceRootDto>();
        Assert.That(dto, Is.Not.Null);
        Assert.That(dto!.Effective, Is.Not.Empty);
        Assert.That(dto.Source, Is.AnyOf("configured", "auto-detected", "cwd-fallback"));
    }

    private sealed record SourceWindowDto(string File, int Line, int StartLine, int EndLine, string[] Lines);
    private sealed record OpenInEditorResultDto(bool Ok, string Error, string Hint);
    private sealed record WorkspaceRootDto(string Effective, string Source);
}
