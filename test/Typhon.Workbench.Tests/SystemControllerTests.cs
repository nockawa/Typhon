using System.Net;
using System.Net.Http.Json;
using System.Runtime.InteropServices;
using NUnit.Framework;

namespace Typhon.Workbench.Tests;

[TestFixture]
public sealed class SystemControllerTests
{
    private WorkbenchFactory _factory;
    private HttpClient _client;

    [SetUp]
    public void SetUp()
    {
        _factory = new WorkbenchFactory();
        _client = _factory.CreateClient(); // ungated
    }

    [TearDown]
    public void TearDown() => _factory.Dispose();

    [Test]
    public async Task GetOs_ReturnsCurrentPlatform()
    {
        var resp = await _client.GetAsync("/api/system/os");
        Assert.That(resp.StatusCode, Is.EqualTo(HttpStatusCode.OK));

        var info = await resp.Content.ReadFromJsonAsync<OsInfoDto>();
        Assert.That(info, Is.Not.Null);

        var expected =
            RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? "windows"
            : RuntimeInformation.IsOSPlatform(OSPlatform.OSX) ? "macos"
            : RuntimeInformation.IsOSPlatform(OSPlatform.Linux) ? "linux"
            : "other";

        Assert.That(info!.Os, Is.EqualTo(expected));
    }

    private sealed record OsInfoDto(string Os);
}
