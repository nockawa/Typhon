using System.Net;
using System.Text.Json;
using NUnit.Framework;

namespace Typhon.Workbench.Tests;

[TestFixture]
public sealed class HealthControllerTests
{
    private WorkbenchFactory _factory;
    private HttpClient _client;

    [SetUp]
    public void SetUp()
    {
        _factory = new WorkbenchFactory();
        _client = _factory.CreateClient();
    }

    [TearDown]
    public void TearDown() => _factory.Dispose();

    [Test]
    public async Task GetHealth_Returns200WithPhase2()
    {
        var response = await _client.GetAsync("/health");
        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));

        var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.That(json.RootElement.GetProperty("status").GetString(), Is.EqualTo("ok"));
        Assert.That(json.RootElement.GetProperty("phase").GetInt32(), Is.EqualTo(2));
    }
}
