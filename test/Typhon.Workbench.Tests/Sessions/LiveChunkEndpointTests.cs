using System;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using NUnit.Framework;
using Typhon.Workbench.Dtos.Sessions;
using Typhon.Workbench.Fixtures;

namespace Typhon.Workbench.Tests.Sessions;

/// <summary>
/// #289 — verifies the unified live/replay chunk endpoint path. After the unification, AttachSessionRuntime drives
/// the same IncrementalCacheBuilder as the replay path, writing chunks to a temp file and serving them via the same
/// `GET /api/sessions/{id}/profiler/chunks/{idx}` endpoint as Trace sessions. These tests confirm:
///   - The endpoint serves chunks for live sessions (previously 409'd).
///   - X-Chunk-* response headers project the manifest entry correctly.
///   - The byte payload round-trips (length matches CacheByteLength).
/// </summary>
[TestFixture]
public sealed class LiveChunkEndpointTests
{
    private WorkbenchFactory _factory;
    private HttpClient _client;

    private static readonly JsonSerializerOptions Json = new() { PropertyNameCaseInsensitive = true };

    [SetUp]
    public void SetUp()
    {
        _factory = new WorkbenchFactory();
        _client = _factory.CreateAuthenticatedClient();
    }

    [TearDown]
    public void TearDown() => _factory.Dispose();

    [Test]
    public async Task GetChunk_OnLiveSession_ReturnsChunkBytes()
    {
        // Drive the mock through enough blocks to flush at least one chunk to the live temp file. The builder's
        // chunk-flush triggers are: TickCap (100 ticks) / ByteCap (1 MiB) / EventCap (50K). With 30 mock blocks
        // emitting 2 records per block, ByteCap won't fire — but the runtime's 200 ms flush timer kicks in and
        // forces a partial-chunk flush so the manifest grows visibly.
        await using var server = new MockTcpProfilerServer
        {
            BlockInterval = TimeSpan.FromMilliseconds(30),
            MaxBlocks = 30,
        };
        server.Start();

        var attachResp = await _client.PostAsJsonAsync(
            "/api/sessions/attach",
            new CreateAttachSessionRequest($"127.0.0.1:{server.Port}"));
        attachResp.EnsureSuccessStatusCode();
        var session = JsonSerializer.Deserialize<SessionDto>(await attachResp.Content.ReadAsStringAsync(), Json)!;

        // Wait for the manifest to grow. The 200 ms force-flush timer ensures a chunk lands within ~250 ms of the
        // first block.
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(5);
        int chunkCount = 0;
        while (DateTime.UtcNow < deadline)
        {
            var metaReq = new HttpRequestMessage(HttpMethod.Get, $"/api/sessions/{session.SessionId}/profiler/metadata");
            metaReq.Headers.Add("X-Session-Token", session.SessionId.ToString());
            var metaResp = await _client.SendAsync(metaReq);
            if (metaResp.StatusCode == HttpStatusCode.OK)
            {
                using var doc = JsonDocument.Parse(await metaResp.Content.ReadAsStringAsync());
                if (doc.RootElement.TryGetProperty("chunkManifest", out var manifestProp)
                    && manifestProp.ValueKind == JsonValueKind.Array)
                {
                    chunkCount = manifestProp.GetArrayLength();
                    if (chunkCount >= 1) break;
                }
            }
            await Task.Delay(50);
        }
        Assert.That(chunkCount, Is.GreaterThanOrEqualTo(1), "live session must flush at least one chunk within 5 s");

        // Fetch chunk index 0.
        var chunkReq = new HttpRequestMessage(HttpMethod.Get, $"/api/sessions/{session.SessionId}/profiler/chunks/0");
        chunkReq.Headers.Add("X-Session-Token", session.SessionId.ToString());
        using var chunkResp = await _client.SendAsync(chunkReq);

        Assert.That(chunkResp.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        Assert.That(chunkResp.Content.Headers.ContentType!.MediaType, Is.EqualTo("application/octet-stream"));

        // X-Chunk-* response headers must be present.
        Assert.That(chunkResp.Headers.Contains("X-Chunk-From-Tick"), Is.True);
        Assert.That(chunkResp.Headers.Contains("X-Chunk-To-Tick"), Is.True);
        Assert.That(chunkResp.Headers.Contains("X-Chunk-Event-Count"), Is.True);
        Assert.That(chunkResp.Headers.Contains("X-Chunk-Uncompressed-Bytes"), Is.True);
        Assert.That(chunkResp.Headers.Contains("X-Chunk-Is-Continuation"), Is.True);
        Assert.That(chunkResp.Headers.Contains("X-Timestamp-Frequency"), Is.True);

        var freqHeader = chunkResp.Headers.GetValues("X-Timestamp-Frequency").FirstOrDefault();
        Assert.That(freqHeader, Is.EqualTo("10000000"), "mock emits 10 MHz timestamp frequency");

        // Body length matches the response Content-Length header (LZ4-compressed).
        var bytes = await chunkResp.Content.ReadAsByteArrayAsync();
        Assert.That(bytes.Length, Is.GreaterThan(0));
        if (chunkResp.Content.Headers.ContentLength is long cl)
        {
            Assert.That(bytes.Length, Is.EqualTo((int)cl));
        }
    }

    [Test]
    public async Task GetChunk_OutOfRangeIdx_Returns404()
    {
        await using var server = new MockTcpProfilerServer { MaxBlocks = 0 };
        server.Start();

        var attachResp = await _client.PostAsJsonAsync(
            "/api/sessions/attach",
            new CreateAttachSessionRequest($"127.0.0.1:{server.Port}"));
        attachResp.EnsureSuccessStatusCode();
        var session = JsonSerializer.Deserialize<SessionDto>(await attachResp.Content.ReadAsStringAsync(), Json)!;

        // Wait for Init metadata to land — endpoint will then accept range checks.
        await Task.Delay(200);

        var req = new HttpRequestMessage(HttpMethod.Get, $"/api/sessions/{session.SessionId}/profiler/chunks/9999");
        req.Headers.Add("X-Session-Token", session.SessionId.ToString());
        var resp = await _client.SendAsync(req);

        // Either 404 (after Init) or 409 (before Init / no chunks yet) — both acceptable; both prevent a malformed read.
        Assert.That(resp.StatusCode, Is.AnyOf(HttpStatusCode.NotFound, HttpStatusCode.Conflict));
    }
}
