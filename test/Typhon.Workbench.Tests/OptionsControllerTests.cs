using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using NUnit.Framework;
using Typhon.Workbench.Hosting;

namespace Typhon.Workbench.Tests;

[TestFixture]
public sealed class OptionsControllerTests
{
    private WorkbenchFactory _factory;
    private HttpClient _client;

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    [SetUp]
    public void SetUp()
    {
        _factory = new WorkbenchFactory();
        _client = _factory.CreateAuthenticatedClient();
    }

    [TearDown]
    public void TearDown() => _factory.Dispose();

    [Test]
    public async Task Get_ReturnsDefaults_OnFreshFactory()
    {
        var resp = await _client.GetAsync("/api/options");
        Assert.That(resp.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        var opts = await resp.Content.ReadFromJsonAsync<WorkbenchOptions>(JsonOpts);
        Assert.That(opts, Is.Not.Null);
        Assert.That(opts!.Editor.Kind, Is.EqualTo(EditorKind.VsCode));
        Assert.That(opts.Editor.CustomCommand, Is.EqualTo(""));
        Assert.That(opts.Profiler.WorkspaceRoot, Is.EqualTo(""));
    }

    [Test]
    public async Task Patch_Editor_RoundTrips()
    {
        var patch = new EditorOptions { Kind = EditorKind.Rider, CustomCommand = "" };
        var resp = await _client.PatchAsJsonAsync("/api/options/editor", patch, JsonOpts);
        Assert.That(resp.StatusCode, Is.EqualTo(HttpStatusCode.OK));

        // Subsequent GET reflects the change.
        var opts = await _client.GetFromJsonAsync<WorkbenchOptions>("/api/options", JsonOpts);
        Assert.That(opts!.Editor.Kind, Is.EqualTo(EditorKind.Rider));
        // Other categories untouched by editor patch.
        Assert.That(opts.Profiler.WorkspaceRoot, Is.EqualTo(""));
    }

    [Test]
    public async Task Patch_Profiler_RoundTrips()
    {
        var patch = new ProfilerOptions { WorkspaceRoot = "/tmp/some-workspace" };
        var resp = await _client.PatchAsJsonAsync("/api/options/profiler", patch, JsonOpts);
        Assert.That(resp.StatusCode, Is.EqualTo(HttpStatusCode.OK));

        var opts = await _client.GetFromJsonAsync<WorkbenchOptions>("/api/options", JsonOpts);
        Assert.That(opts!.Profiler.WorkspaceRoot, Is.EqualTo("/tmp/some-workspace"));
        Assert.That(opts.Editor.Kind, Is.EqualTo(EditorKind.VsCode), "Editor should be untouched by profiler patch.");
    }

    [Test]
    public async Task Put_FullReplace_Works()
    {
        var fullDoc = new WorkbenchOptions
        {
            Editor = new EditorOptions { Kind = EditorKind.Cursor, CustomCommand = "" },
            Profiler = new ProfilerOptions { WorkspaceRoot = "/repo/typhon" },
        };
        var resp = await _client.PutAsJsonAsync("/api/options", fullDoc, JsonOpts);
        Assert.That(resp.StatusCode, Is.EqualTo(HttpStatusCode.OK));

        var opts = await _client.GetFromJsonAsync<WorkbenchOptions>("/api/options", JsonOpts);
        Assert.That(opts!.Editor.Kind, Is.EqualTo(EditorKind.Cursor));
        Assert.That(opts.Profiler.WorkspaceRoot, Is.EqualTo("/repo/typhon"));
    }

    [TestCase("vsCode", EditorKind.VsCode)]
    [TestCase("cursor", EditorKind.Cursor)]
    [TestCase("rider", EditorKind.Rider)]
    [TestCase("visualStudio", EditorKind.VisualStudio)]
    [TestCase("custom", EditorKind.Custom)]
    public async Task Patch_Editor_AcceptsCamelCaseEnumString(string wireKind, EditorKind expected)
    {
        // The TS client sends `{"kind": "rider"}` (camelCase string), not an integer. This
        // regressed when JsonStringEnumConverter wasn't registered on the MVC pipeline,
        // returning 400 to every Save click on the Editor options form.
        var json = $$"""{"kind":"{{wireKind}}","customCommand":""}""";
        var content = new StringContent(json, System.Text.Encoding.UTF8, "application/json");
        var resp = await _client.PatchAsync("/api/options/editor", content);
        Assert.That(resp.StatusCode, Is.EqualTo(HttpStatusCode.OK), await resp.Content.ReadAsStringAsync());

        var opts = await _client.GetFromJsonAsync<WorkbenchOptions>("/api/options", JsonOpts);
        Assert.That(opts!.Editor.Kind, Is.EqualTo(expected));
    }

    [Test]
    public async Task Get_UnauthenticatedClient_Returns401()
    {
        // The default factory client doesn't carry the bootstrap token.
        var unauth = _factory.CreateClient();
        var resp = await unauth.GetAsync("/api/options");
        Assert.That(resp.StatusCode, Is.EqualTo(HttpStatusCode.Unauthorized));
    }
}
