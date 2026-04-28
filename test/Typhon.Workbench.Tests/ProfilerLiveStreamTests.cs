using System;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using NUnit.Framework;
using Typhon.Workbench.Dtos.Sessions;
using Typhon.Workbench.Fixtures;

namespace Typhon.Workbench.Tests;

/// <summary>
/// End-to-end coverage for the profiler live-stream SSE endpoint. The client's useEventSource
/// hook branches on the <c>kind</c> discriminant — <c>"metadata"</c> / <c>"tick"</c> /
/// <c>"heartbeat"</c> — so a regression that drops a field or renames the kind would silently
/// break the attach-mode panel. These tests pin the exact frame shape emitted for each kind.
/// </summary>
[TestFixture]
public sealed class ProfilerLiveStreamTests
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
    public async Task Stream_AfterAttach_EmitsMetadata_Heartbeat_AndDeltaFrames()
    {
        // #289 — post-unification, the live SSE stream uses growth deltas instead of per-tick batches:
        //   - "metadata" full snapshot on connect (kind metadata)
        //   - "tickSummaryAdded" / "chunkAdded" / "globalMetricsUpdated" growth deltas
        //   - "heartbeat" status frames
        await using var server = new MockTcpProfilerServer
        {
            BlockInterval = TimeSpan.FromMilliseconds(40),
            MaxBlocks = 50,
        };
        server.Start();

        var attachResp = await _client.PostAsJsonAsync(
            "/api/sessions/attach",
            new CreateAttachSessionRequest($"127.0.0.1:{server.Port}"));
        attachResp.EnsureSuccessStatusCode();
        var session = JsonSerializer.Deserialize<SessionDto>(await attachResp.Content.ReadAsStringAsync(), Json)!;

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(8));
        var req = new HttpRequestMessage(HttpMethod.Get, $"/api/sessions/{session.SessionId}/profiler/stream");
        req.Headers.Add("X-Session-Token", session.SessionId.ToString());

        using var resp = await _client.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, cts.Token);
        Assert.That(resp.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        Assert.That(resp.Content.Headers.ContentType!.MediaType, Is.EqualTo("text/event-stream"));

        var seenMetadata = false;
        var seenHeartbeat = false;
        var seenTickSummaryAdded = false;
        string tickJson = null;
        string metadataJson = null;

        using var stream = await resp.Content.ReadAsStreamAsync(cts.Token);
        using var reader = new StreamReader(stream);
        while (!(seenMetadata && seenHeartbeat && seenTickSummaryAdded))
        {
            var line = await reader.ReadLineAsync(cts.Token);
            if (line == null) break;
            if (!line.StartsWith("data:", StringComparison.Ordinal)) continue;

            var payload = line[5..].TrimStart();
            using var doc = JsonDocument.Parse(payload);
            var kind = doc.RootElement.GetProperty("kind").GetString();

            switch (kind)
            {
                case "metadata":
                    seenMetadata = true;
                    metadataJson = payload;
                    break;
                case "heartbeat":
                    seenHeartbeat = true;
                    break;
                case "tickSummaryAdded":
                    seenTickSummaryAdded = true;
                    tickJson = payload;
                    break;
            }
        }

        Assert.That(seenMetadata, Is.True, "client expects a metadata frame on connect");
        Assert.That(seenHeartbeat, Is.True, "client expects a heartbeat frame with the initial connection status");
        Assert.That(seenTickSummaryAdded, Is.True, "client expects at least one tickSummaryAdded delta from block-frame ingest");

        using (var metaDoc = JsonDocument.Parse(metadataJson!))
        {
            Assert.That(metaDoc.RootElement.GetProperty("kind").GetString(), Is.EqualTo("metadata"));
            Assert.That(metaDoc.RootElement.TryGetProperty("metadata", out var metaProp), Is.True,
                "metadata frame must carry a non-null metadata sub-object");
            Assert.That(metaProp.GetProperty("header").GetProperty("timestampFrequency").GetInt64(),
                Is.EqualTo(10_000_000),
                "mock emits 10 MHz timestamp frequency; regression here would drop decoding precision on the client");
        }

        using (var tickDoc = JsonDocument.Parse(tickJson!))
        {
            Assert.That(tickDoc.RootElement.GetProperty("kind").GetString(), Is.EqualTo("tickSummaryAdded"));
            Assert.That(tickDoc.RootElement.TryGetProperty("tickSummary", out var summaryProp), Is.True,
                "tickSummaryAdded frame must carry a non-null tickSummary sub-object");
            Assert.That(summaryProp.GetProperty("tickNumber").GetUInt32(), Is.GreaterThan(0u));
        }
    }

    [Test]
    public async Task Stream_NonAttachSession_Returns401()
    {
        // Trace sessions aren't valid subjects of the live stream — the handler casts to
        // AttachSession and bails with 401 if the session isn't one. Pins the upstream auth
        // boundary.
        var tracePath = TraceFixtureBuilder.BuildMinimalTrace(_factory.DemoDirectory, tickCount: 2, instantsPerTick: 1);
        var resp = await _client.PostAsJsonAsync("/api/sessions/trace", new CreateTraceSessionRequest(tracePath));
        resp.EnsureSuccessStatusCode();
        var session = JsonSerializer.Deserialize<SessionDto>(await resp.Content.ReadAsStringAsync(), Json)!;

        var req = new HttpRequestMessage(HttpMethod.Get, $"/api/sessions/{session.SessionId}/profiler/stream");
        req.Headers.Add("X-Session-Token", session.SessionId.ToString());
        var streamResp = await _client.SendAsync(req, HttpCompletionOption.ResponseHeadersRead);
        Assert.That(streamResp.StatusCode, Is.EqualTo(HttpStatusCode.Unauthorized));
    }
}
