using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using NUnit.Framework;
using Typhon.Workbench.Dtos.Fs;

namespace Typhon.Workbench.Tests.Fs;

[TestFixture]
public sealed class FileSystemControllerTests
{
    private WorkbenchFactory _factory;
    private HttpClient _client;
    private string _fixtureDir;

    [SetUp]
    public void SetUp()
    {
        _factory = new WorkbenchFactory();
        _client = _factory.CreateAuthenticatedClient();
        _fixtureDir = Path.Combine(Path.GetTempPath(), "typhon-wb-fs-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_fixtureDir);
        File.WriteAllText(Path.Combine(_fixtureDir, "data.typhon"), "fake db");
        File.WriteAllText(Path.Combine(_fixtureDir, "MyGame.schema.dll"), "not a real dll");
        Directory.CreateDirectory(Path.Combine(_fixtureDir, "subdir"));
    }

    [TearDown]
    public void TearDown()
    {
        _factory.Dispose();
        try { if (Directory.Exists(_fixtureDir)) Directory.Delete(_fixtureDir, recursive: true); }
        catch { /* best-effort */ }
    }

    private static readonly JsonSerializerOptions Json = new() { PropertyNameCaseInsensitive = true };

    [Test]
    public async Task Home_ReturnsExistingDirectory()
    {
        var resp = await _client.GetAsync("/api/fs/home");
        Assert.That(resp.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        var entry = JsonSerializer.Deserialize<FileEntryDto>(await resp.Content.ReadAsStringAsync(), Json)!;
        Assert.That(entry.Kind, Is.EqualTo("dir"));
        Assert.That(Directory.Exists(entry.FullPath), Is.True);
    }

    [Test]
    public async Task List_FlagsSchemaDll()
    {
        var resp = await _client.GetAsync($"/api/fs/list?path={Uri.EscapeDataString(_fixtureDir)}");
        Assert.That(resp.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        var listing = JsonSerializer.Deserialize<DirectoryListingDto>(await resp.Content.ReadAsStringAsync(), Json)!;

        var schema = Array.Find(listing.Entries, e => e.Name == "MyGame.schema.dll");
        Assert.That(schema, Is.Not.Null);
        Assert.That(schema!.IsSchemaDll, Is.True);

        var typhon = Array.Find(listing.Entries, e => e.Name == "data.typhon");
        Assert.That(typhon, Is.Not.Null);
        Assert.That(typhon!.IsSchemaDll, Is.False);

        var sub = Array.Find(listing.Entries, e => e.Name == "subdir");
        Assert.That(sub, Is.Not.Null);
        Assert.That(sub!.Kind, Is.EqualTo("dir"));
    }

    [Test]
    public async Task List_NonExistentPath_Returns404()
    {
        var resp = await _client.GetAsync("/api/fs/list?path=/nonexistent-dir-xyz");
        Assert.That(resp.StatusCode, Is.EqualTo(HttpStatusCode.NotFound));
    }

    [Test]
    public async Task Stat_File_ReturnsKindFile()
    {
        var path = Path.Combine(_fixtureDir, "data.typhon");
        var resp = await _client.GetAsync($"/api/fs/stat?path={Uri.EscapeDataString(path)}");
        Assert.That(resp.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        var entry = JsonSerializer.Deserialize<FileEntryDto>(await resp.Content.ReadAsStringAsync(), Json)!;
        Assert.That(entry.Kind, Is.EqualTo("file"));
    }

    // ── Bootstrap-token gate (T4-3) ───────────────────────────────────────────────────────────
    // The /api/fs/* endpoints expose the user's filesystem. Per CLAUDE.md's threat model there's
    // no path sandbox — a single user is the only client, and the bootstrap token is the sole
    // defense against a malicious webpage fetching the user's files. Pin that contract so a
    // regression that drops [RequireBootstrapToken] from the controller is caught immediately.

    [Test]
    public async Task Home_Unauthed_Returns401()
    {
        using var unauthed = _factory.CreateClient(); // no bootstrap-token handler
        var resp = await unauthed.GetAsync("/api/fs/home");
        Assert.That(resp.StatusCode, Is.EqualTo(HttpStatusCode.Unauthorized));
    }

    [Test]
    public async Task List_Unauthed_Returns401_EvenForNonexistentPath()
    {
        // Auth gate must fire BEFORE the 404 "path not found" path — otherwise a stranger could
        // probe filesystem existence via differential timing on 401 vs 404.
        using var unauthed = _factory.CreateClient();
        var resp = await unauthed.GetAsync("/api/fs/list?path=/definitely-not-here-xyz");
        Assert.That(resp.StatusCode, Is.EqualTo(HttpStatusCode.Unauthorized));
    }

    [Test]
    public async Task Stat_Unauthed_Returns401()
    {
        using var unauthed = _factory.CreateClient();
        var path = Path.Combine(_fixtureDir, "data.typhon");
        var resp = await unauthed.GetAsync($"/api/fs/stat?path={Uri.EscapeDataString(path)}");
        Assert.That(resp.StatusCode, Is.EqualTo(HttpStatusCode.Unauthorized));
    }

    [Test]
    public async Task List_WrongToken_Returns401()
    {
        // A stranger who guesses the header name but not the value must still be blocked. Exact
        // token comparison is documented as fixed-time in BootstrapTokenGate; this just proves
        // the gate is actually consulting it.
        using var unauthed = _factory.CreateClient();
        var req = new HttpRequestMessage(HttpMethod.Get, $"/api/fs/list?path={Uri.EscapeDataString(_fixtureDir)}");
        req.Headers.Add("X-Workbench-Token", "deadbeef");
        var resp = await unauthed.SendAsync(req);
        Assert.That(resp.StatusCode, Is.EqualTo(HttpStatusCode.Unauthorized));
    }
}
