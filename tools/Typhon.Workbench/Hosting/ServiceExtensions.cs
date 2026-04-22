using Typhon.Workbench.Fs;
using Typhon.Workbench.Security;
using Typhon.Workbench.Sessions;
using Typhon.Workbench.Streams;

namespace Typhon.Workbench.Hosting;

public static class ServiceExtensions
{
    public static IServiceCollection AddWorkbenchServices(this IServiceCollection services)
    {
        services.AddSingleton<BootstrapTokenGate>();
        services.AddSingleton<SessionManager>();
        services.AddSingleton<DemoDataProvider>();
        services.AddSingleton<FileBrowserService>();
        return services;
    }

    public static IEndpointRouteBuilder MapWorkbenchEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/sessions/{sessionId:guid}/heartbeat", HeartbeatStream.HandleAsync)
           .WithTags("Sessions");
        app.MapGet("/api/sessions/{sessionId:guid}/resources/stream", ResourceGraphStream.HandleAsync)
           .WithTags("Resources");
        return app;
    }

    /// <summary>
    /// Registers a shutdown callback that disposes every live session — critical for releasing MMF
    /// file handles before the process exits.
    /// </summary>
    public static void RegisterSessionShutdownHook(this IServiceProvider services)
    {
        var lifetime = services.GetRequiredService<IHostApplicationLifetime>();
        var manager = services.GetRequiredService<SessionManager>();
        lifetime.ApplicationStopping.Register(manager.DisposeAll);
    }
}
