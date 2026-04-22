using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using NUnit.Framework;
using Typhon.Engine;
using Typhon.Workbench.Dtos.Sessions;
using Typhon.Workbench.Sessions;

namespace Typhon.Workbench.Tests;

[TestFixture]
public sealed class ResourceGraphStreamTests
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
    public async Task ResourceStream_NoToken_Returns401()
    {
        var response = await _client.GetAsync($"/api/sessions/{Guid.NewGuid()}/resources/stream");
        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.Unauthorized));
    }

    [Test]
    public async Task ResourceStream_ValidToken_OpensAsSse()
    {
        var session = await CreateSessionAsync();

        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(300));
        var req = new HttpRequestMessage(HttpMethod.Get, $"/api/sessions/{session.SessionId}/resources/stream");
        req.Headers.Add("X-Session-Token", session.SessionId.ToString());

        // SSE endpoints never signal EOF on their own — we read until our cancellation fires.
        var response = await _client.SendAsync(req, HttpCompletionOption.ResponseHeadersRead);
        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        Assert.That(response.Content.Headers.ContentType?.MediaType, Is.EqualTo("text/event-stream"));

        try
        {
            await using var stream = await response.Content.ReadAsStreamAsync(cts.Token);
            var buf = new byte[256];
            await stream.ReadAsync(buf.AsMemory(0, buf.Length), cts.Token);
        }
        catch (OperationCanceledException)
        {
            // Expected — we cancel to end the never-closing SSE.
        }
    }

    /// <summary>
    /// Exit-criterion for Phase 6 #252: "10/sec rate limit verified by stress test". Under a burst
    /// of hundreds of node-added mutations in a short window, <see cref="Typhon.Workbench.Streams.ResourceGraphStream"/>'s
    /// leading-edge + coalescing flush loop must cap the FLUSH cadence at ≤ 10/sec (100 ms
    /// post-flush coalescing window). Each flush can carry many distinct-id DTOs — the bound is
    /// on flush cycles, not on event count, because the flush cadence is what governs wake-ups,
    /// socket writes, and client-side processing overhead.
    ///
    /// We detect flush boundaries client-side by timing: DTOs inside one flush arrive back-to-back
    /// (same `Response.Body.FlushAsync`), so an inter-line gap &gt; 50 ms marks a new flush.
    /// </summary>
    [Test]
    [CancelAfter(15_000)]
    public async Task ResourceStream_BurstMutations_FlushCadenceBoundedBy10PerSec(CancellationToken testCt)
    {
        var session = await CreateSessionAsync();

        // Reach into the test host's SessionManager to get the live registry — the only way to
        // synthesize mutation events without running the engine's own component-registration flow.
        var sessions = _factory.Services.GetRequiredService<SessionManager>();
        Assume.That(sessions.TryGet(session.SessionId, out var wbSession) && wbSession is OpenSession, Is.True);
        var registry = ((OpenSession)wbSession!).Engine.Registry;

        var req = new HttpRequestMessage(HttpMethod.Get, $"/api/sessions/{session.SessionId}/resources/stream");
        req.Headers.Add("X-Session-Token", session.SessionId.ToString());

        using var response = await _client.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, testCt);
        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));

        await using var stream = await response.Content.ReadAsStreamAsync(testCt);
        using var reader = new StreamReader(stream);

        // Sustained burst: 300 distinct-id nodes over ~400 ms. Each new node raises a unique
        // (Kind, NodeId) which survives coalescing → drives the flush loop at its max cadence.
        var spawned = new List<IResource>();
        var spawnTask = Task.Run(async () =>
        {
            for (int i = 0; i < 300; i++)
            {
                spawned.Add(new ResourceNode($"stress-{i}", ResourceType.Node, registry.Storage));
                if ((i + 1) % 15 == 0)
                {
                    await Task.Delay(20, testCt);
                }
            }
        }, testCt);

        // Record the timestamp of each `data:` line. Group lines into flush bursts using an
        // inter-line gap threshold (50 ms) — well below the stream's 100 ms post-flush window
        // but well above the in-burst inter-line spacing (microseconds on localhost).
        const long GapThresholdMs = 50;
        var arrivalTicksSw = new List<long>();
        using var window = CancellationTokenSource.CreateLinkedTokenSource(testCt);
        window.CancelAfter(TimeSpan.FromSeconds(1));
        try
        {
            while (!window.IsCancellationRequested)
            {
                var line = await reader.ReadLineAsync(window.Token);
                if (line != null && line.StartsWith("data:", StringComparison.Ordinal))
                {
                    arrivalTicksSw.Add(Stopwatch.GetTimestamp());
                }
            }
        }
        catch (OperationCanceledException)
        {
            // 1-second measurement window elapsed — expected.
        }

        await spawnTask;

        // Clean up spawned nodes so the session tears down cleanly.
        foreach (var node in spawned)
        {
            registry.Storage.RemoveChild(node);
        }

        // Count flushes: each gap > threshold between consecutive `data:` lines = new flush.
        var gapTicks = (Stopwatch.Frequency * GapThresholdMs) / 1000;
        var flushCount = arrivalTicksSw.Count == 0 ? 0 : 1;
        for (int i = 1; i < arrivalTicksSw.Count; i++)
        {
            if (arrivalTicksSw[i] - arrivalTicksSw[i - 1] > gapTicks)
            {
                flushCount++;
            }
        }

        // Spec: flush cadence ≤ 10/sec over any 1-second window. Allow one extra for the
        // leading-edge flush fired immediately at burst start, plus slack of 1 for CI jitter.
        Assert.That(flushCount, Is.LessThanOrEqualTo(12),
            $"ResourceGraphStream flushed {flushCount} times in 1 second under a 300-event burst — exceeds the 10/sec spec (received {arrivalTicksSw.Count} DTOs across those flushes)");
        // Sanity: the stream must actually be delivering flushes, otherwise the upper bound is meaningless.
        Assert.That(flushCount, Is.GreaterThan(0),
            "No flushes detected under a 300-event burst — stream wiring is broken");
    }
}
