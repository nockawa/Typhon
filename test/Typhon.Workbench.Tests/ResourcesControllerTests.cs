using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using NUnit.Framework;
using Typhon.Workbench.Dtos.Resources;
using Typhon.Workbench.Dtos.Sessions;

namespace Typhon.Workbench.Tests;

[TestFixture]
public sealed class ResourcesControllerTests
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

    private async Task<SessionDto> CreateSessionAsync()
    {
        var resp = await _client.PostAsJsonAsync("/api/sessions/file", new CreateFileSessionRequest("demo.typhon"));
        resp.EnsureSuccessStatusCode();
        return JsonSerializer.Deserialize<SessionDto>(await resp.Content.ReadAsStringAsync(), Json)!;
    }

    [Test]
    public async Task GetRoot_NoToken_Returns401()
    {
        var response = await _client.GetAsync($"/api/sessions/{Guid.NewGuid()}/resources/root");
        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.Unauthorized));
    }

    [Test]
    public async Task GetRoot_ValidToken_ReturnsRealEngineGraph()
    {
        var session = await CreateSessionAsync();

        var req = new HttpRequestMessage(HttpMethod.Get, $"/api/sessions/{session.SessionId}/resources/root");
        req.Headers.Add("X-Session-Token", session.SessionId.ToString());
        var response = await _client.SendAsync(req);

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));

        var graph = JsonSerializer.Deserialize<ResourceGraphDto>(
            await response.Content.ReadAsStringAsync(), Json);

        Assert.That(graph, Is.Not.Null);
        Assert.That(graph!.Root, Is.Not.Null);
        Assert.That(graph.Root.Children, Is.Not.Null.And.Not.Empty,
            "Real engine should expose at least one subsystem (Storage / DataEngine / etc.)");
        // Any real engine exposes recognizable subsystem names; we don't pin to a specific list
        // because the registry tree can evolve. Just assert Types are all non-null strings.
        foreach (var child in graph.Root.Children)
        {
            Assert.That(child.Type, Is.Not.Null.And.Not.Empty);
            Assert.That(child.Id, Is.Not.Null.And.Not.Empty);
        }
    }

    [Test]
    public async Task GetRoot_DepthAll_ReturnsFullTree()
    {
        var session = await CreateSessionAsync();

        var req = new HttpRequestMessage(HttpMethod.Get,
            $"/api/sessions/{session.SessionId}/resources/root?depth=all");
        req.Headers.Add("X-Session-Token", session.SessionId.ToString());
        var response = await _client.SendAsync(req);

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));

        var graph = JsonSerializer.Deserialize<ResourceGraphDto>(
            await response.Content.ReadAsStringAsync(), Json);

        Assert.That(graph, Is.Not.Null);
        // Walk the whole tree — every node we visit must have its Children array populated
        // (depth=all means no truncation; children are either an empty array or a real list,
        //  never null).
        int visited = 0;
        void Walk(ResourceNodeDto node)
        {
            visited++;
            Assert.That(node.Children, Is.Not.Null, $"depth=all left null children on {node.Id}");
            foreach (var c in node.Children!) Walk(c);
        }
        Walk(graph!.Root);
        Assert.That(visited, Is.GreaterThan(1), "Real engine graph should have more than just the root at depth=all");
    }

    [Test]
    public async Task GetRoot_InvalidDepth_Returns400()
    {
        var session = await CreateSessionAsync();
        var req = new HttpRequestMessage(HttpMethod.Get,
            $"/api/sessions/{session.SessionId}/resources/root?depth=garbage");
        req.Headers.Add("X-Session-Token", session.SessionId.ToString());
        var response = await _client.SendAsync(req);

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest));
    }

    [Test]
    public async Task GetChildren_UnknownId_Returns404()
    {
        var session = await CreateSessionAsync();

        var req = new HttpRequestMessage(HttpMethod.Get, $"/api/sessions/{session.SessionId}/resources/no-such-node/children");
        req.Headers.Add("X-Session-Token", session.SessionId.ToString());
        var response = await _client.SendAsync(req);

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.NotFound));
    }

    [Test]
    public async Task GetChildren_RootId_ReturnsSubsystemChildren()
    {
        var session = await CreateSessionAsync();

        // First get root to learn its Id
        var rootReq = new HttpRequestMessage(HttpMethod.Get, $"/api/sessions/{session.SessionId}/resources/root");
        rootReq.Headers.Add("X-Session-Token", session.SessionId.ToString());
        var rootResp = await _client.SendAsync(rootReq);
        var rootGraph = JsonSerializer.Deserialize<ResourceGraphDto>(await rootResp.Content.ReadAsStringAsync(), Json)!;

        var childrenReq = new HttpRequestMessage(HttpMethod.Get,
            $"/api/sessions/{session.SessionId}/resources/{rootGraph.Root.Id}/children");
        childrenReq.Headers.Add("X-Session-Token", session.SessionId.ToString());
        var childrenResp = await _client.SendAsync(childrenReq);

        Assert.That(childrenResp.StatusCode, Is.EqualTo(HttpStatusCode.OK));

        var children = JsonSerializer.Deserialize<ResourceNodeDto[]>(await childrenResp.Content.ReadAsStringAsync(), Json);
        Assert.That(children, Is.Not.Null.And.Not.Empty);
    }
}
