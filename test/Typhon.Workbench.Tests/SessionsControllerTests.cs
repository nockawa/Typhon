using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using NUnit.Framework;
using Typhon.Workbench.Dtos.Sessions;

namespace Typhon.Workbench.Tests;

[TestFixture]
public sealed class SessionsControllerTests
{
    private WorkbenchFactory _factory;
    private HttpClient _client;

    [SetUp]
    public void SetUp()
    {
        _factory = new WorkbenchFactory();
        _client = _factory.CreateAuthenticatedClient();
    }

    [TearDown]
    public void TearDown() => _factory.Dispose();

    private static readonly JsonSerializerOptions Json = new() { PropertyNameCaseInsensitive = true };

    private async Task<SessionDto> PostDemoAsync()
    {
        var resp = await _client.PostAsJsonAsync("/api/sessions/file", new CreateFileSessionRequest("demo.typhon"));
        resp.EnsureSuccessStatusCode();
        return JsonSerializer.Deserialize<SessionDto>(await resp.Content.ReadAsStringAsync(), Json)!;
    }

    [Test]
    public async Task PostFile_Returns201WithSessionDto()
    {
        var response = await _client.PostAsJsonAsync("/api/sessions/file", new CreateFileSessionRequest("demo.typhon"));
        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.Created));

        var dto = JsonSerializer.Deserialize<SessionDto>(await response.Content.ReadAsStringAsync(), Json);

        Assert.That(dto, Is.Not.Null);
        Assert.That(dto!.SessionId, Is.Not.EqualTo(Guid.Empty));
        Assert.That(dto.Kind, Is.EqualTo("Open"));
        Assert.That(dto.State, Is.EqualTo("Ready"));
        Assert.That(dto.FilePath, Does.EndWith("demo.typhon"));
    }

    [Test]
    public async Task PostFile_MissingSchemaDll_Returns404()
    {
        // Phase 4: explicit schemaDllPaths pointing to a non-existent file → schema_missing (404).
        var request = new CreateFileSessionRequest("demo.typhon", ["/nonexistent/foo.schema.dll"]);
        var response = await _client.PostAsJsonAsync("/api/sessions/file", request);
        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.NotFound));
    }

    [Test]
    public async Task DeleteSession_Returns204()
    {
        var dto = await PostDemoAsync();

        var req = new HttpRequestMessage(HttpMethod.Delete, $"/api/sessions/{dto.SessionId}");
        req.Headers.Add("X-Session-Token", dto.SessionId.ToString());
        var deleteResp = await _client.SendAsync(req);

        Assert.That(deleteResp.StatusCode, Is.EqualTo(HttpStatusCode.NoContent));
    }

    [Test]
    public async Task GetSession_MissingToken_Returns401()
    {
        var response = await _client.GetAsync($"/api/sessions/{Guid.NewGuid()}");
        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.Unauthorized));
    }

    [Test]
    public async Task GetSession_InvalidToken_Returns401()
    {
        var req = new HttpRequestMessage(HttpMethod.Get, $"/api/sessions/{Guid.NewGuid()}");
        req.Headers.Add("X-Session-Token", Guid.NewGuid().ToString());
        var response = await _client.SendAsync(req);
        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.Unauthorized));
    }

    [Test]
    public async Task GetSession_ValidToken_Returns200()
    {
        var dto = await PostDemoAsync();

        var req = new HttpRequestMessage(HttpMethod.Get, $"/api/sessions/{dto.SessionId}");
        req.Headers.Add("X-Session-Token", dto.SessionId.ToString());
        var getResp = await _client.SendAsync(req);

        Assert.That(getResp.StatusCode, Is.EqualTo(HttpStatusCode.OK));
    }
}
