using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Typhon.Workbench.Security;
using Typhon.Workbench.Sessions;

namespace Typhon.Workbench.Tests;

public sealed class WorkbenchFactory : WebApplicationFactory<Program>
{
    public string DemoDirectory { get; } =
        Path.Combine(Path.GetTempPath(), "typhon-wb-tests", Guid.NewGuid().ToString("N"));

    protected override void ConfigureWebHost(Microsoft.AspNetCore.Hosting.IWebHostBuilder builder)
    {
        builder.ConfigureServices(services =>
        {
            // Replace the default DemoDataProvider with one rooted in a per-test temp dir so concurrent
            // tests don't collide on the same .bin file, and cleanup is trivial on dispose.
            services.RemoveAll<DemoDataProvider>();
            services.AddSingleton(_ => new DemoDataProvider(DemoDirectory));

            // Isolate the bootstrap token file to the per-test temp dir. Without this, every test
            // host overwrites %LOCALAPPDATA%\Typhon\Workbench\bootstrap.token — which races with a
            // running dev server (tests stomp its token; dev server stomps tests') and with other
            // parallel tests. Each factory gets its own in-memory token + its own file.
            services.RemoveAll<BootstrapTokenGate>();
            services.AddSingleton(_ => new BootstrapTokenGate(DemoDirectory));

            // Same isolation for OptionsStore — pin its on-disk JSON to the per-test temp dir so
            // tests don't pollute the developer's real LocalApplicationData/Typhon.Workbench/options.json.
            services.RemoveAll<Typhon.Workbench.Hosting.OptionsStore>();
            services.AddSingleton(sp => new Typhon.Workbench.Hosting.OptionsStore(
                sp.GetRequiredService<Microsoft.Extensions.Logging.ILogger<Typhon.Workbench.Hosting.OptionsStore>>(),
                DemoDirectory));
        });
    }

    /// <summary>
    /// HttpClient that auto-attaches the running Workbench's bootstrap token on every request —
    /// mirrors what the Vite dev proxy does in production. Use this for tests that exercise
    /// gated endpoints; tests that specifically verify the gate itself can call
    /// <see cref="WebApplicationFactory{TEntryPoint}.CreateClient()"/> to bypass it.
    /// </summary>
    public HttpClient CreateAuthenticatedClient()
    {
        var client = CreateDefaultClient(new BootstrapTokenHandler(Services.GetRequiredService<BootstrapTokenGate>()));
        return client;
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        if (disposing)
        {
            try
            {
                if (Directory.Exists(DemoDirectory)) Directory.Delete(DemoDirectory, recursive: true);
            }
            catch { /* best-effort cleanup */ }
        }
    }

    private sealed class BootstrapTokenHandler : DelegatingHandler
    {
        private readonly BootstrapTokenGate _gate;

        public BootstrapTokenHandler(BootstrapTokenGate gate) => _gate = gate;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (!request.Headers.Contains(BootstrapTokenGate.HeaderName))
            {
                request.Headers.Add(BootstrapTokenGate.HeaderName, _gate.Token);
            }
            return base.SendAsync(request, cancellationToken);
        }
    }
}
