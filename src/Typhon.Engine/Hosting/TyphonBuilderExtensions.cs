using JetBrains.Annotations;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using System;

namespace Typhon.Engine;

[PublicAPI]
public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddPagedMMF(
        this IServiceCollection services,
        Action<PagedMMFOptions> configure = null) =>
        AddPagedMMF<PagedMMF, PagedMMFOptions>(services, ServiceLifetime.Singleton, configure);

    public static IServiceCollection AddScopedPagedMemoryMappedFile(
        this IServiceCollection services,
        Action<PagedMMFOptions> configure = null) =>
        AddPagedMMF<PagedMMF, PagedMMFOptions>(services, ServiceLifetime.Scoped, configure);

    public static IServiceCollection AddTransientPagedMemoryMappedFile(
        this IServiceCollection services,
        Action<PagedMMFOptions> configure = null) =>
        AddPagedMMF<PagedMMF, PagedMMFOptions>(services, ServiceLifetime.Transient, configure);

    public static IServiceCollection AddManagedPagedMMF(
        this IServiceCollection services,
        Action<ManagedPagedMMFOptions> configure = null) =>
        AddPagedMMF<ManagedPagedMMF, ManagedPagedMMFOptions>(services, ServiceLifetime.Singleton, configure);

    public static IServiceCollection AddScopedManagedPagedMemoryMappedFile(
        this IServiceCollection services,
        Action<ManagedPagedMMFOptions> configure = null) =>
        AddPagedMMF<ManagedPagedMMF, ManagedPagedMMFOptions>(services, ServiceLifetime.Scoped, configure);

    public static IServiceCollection AddTransientManagedPagedMemoryMappedFile(
        this IServiceCollection services,
        Action<ManagedPagedMMFOptions> configure = null) =>
        AddPagedMMF<ManagedPagedMMF, ManagedPagedMMFOptions>(services, ServiceLifetime.Transient, configure);

    public static IServiceCollection AddDatabaseEngine(
        this IServiceCollection services,
        Action<DatabaseEngineOptions> configure = null) =>
        AddDatabaseEngine(services, ServiceLifetime.Singleton, configure);

    public static IServiceCollection AddScopedDatabaseEngine(
        this IServiceCollection services,
        Action<DatabaseEngineOptions> configure = null) =>
        AddDatabaseEngine(services, ServiceLifetime.Scoped, configure);

    public static IServiceCollection AddTransientDatabaseEngine(
        this IServiceCollection services,
        Action<DatabaseEngineOptions> configure = null) =>
        AddDatabaseEngine(services, ServiceLifetime.Transient, configure);

    private static IServiceCollection AddPagedMMF<TS, TO>(
        this IServiceCollection services,
        ServiceLifetime lifetime,
        Action<TO> configure = null) where TS : PagedMMF where TO : PagedMMFOptions
    {
        services.AddOptions<TO>();
        services.TryAddSingleton<TimeManager>();

        var optionsBuilder = services.AddOptions<TO>();

        if (configure != null)
        {
            optionsBuilder.Configure(configure);

            optionsBuilder.Validate(options =>
            {
                
                // TODO Add validation logic for PagedMemoryMappedFileOptions
                return true;
            });
        }

        var serviceDescriptor = lifetime switch
        {
            ServiceLifetime.Singleton => ServiceDescriptor.Singleton(CreatePagedMemoryMappedFile<TS, TO>),
            ServiceLifetime.Scoped => ServiceDescriptor.Scoped(CreatePagedMemoryMappedFile<TS, TO>),
            ServiceLifetime.Transient => ServiceDescriptor.Transient(CreatePagedMemoryMappedFile<TS, TO>),
            _ => throw new ArgumentOutOfRangeException(nameof(lifetime), lifetime, "Invalid service lifetime specified.")
        };

        services.Add(serviceDescriptor);
        return services;
    }

    private static TS CreatePagedMemoryMappedFile<TS, TO>(IServiceProvider serviceProvider) where TS : PagedMMF where TO : PagedMMFOptions
    {
        try
        {
            var options = serviceProvider.GetRequiredService<IOptions<TO>>();
            var timeManager = serviceProvider.GetRequiredService<TimeManager>();
            var logger = serviceProvider.GetRequiredService<ILogger<TS>>();
            
            return (TS)Activator.CreateInstance(typeof(TS), serviceProvider, options.Value, timeManager, logger);
        }
        catch (Exception e)
        {
            Console.WriteLine(e);
            throw;
        }
    }

    private static IServiceCollection AddDatabaseEngine(IServiceCollection services, ServiceLifetime lifetime, Action<DatabaseEngineOptions> configure)
    {
        var optionsBuilder = services.AddOptions<DatabaseEngineOptions>();

        if (configure != null)
        {
            optionsBuilder.Configure(configure);

            optionsBuilder.Validate(options =>
            {
                
                // TODO Add validation logic for PagedMemoryMappedFileOptions
                return true;
            });
        }

        var serviceDescriptor = lifetime switch
        {
            ServiceLifetime.Singleton => ServiceDescriptor.Singleton(CreateDatabaseEngine),
            ServiceLifetime.Scoped => ServiceDescriptor.Scoped(CreateDatabaseEngine),
            ServiceLifetime.Transient => ServiceDescriptor.Transient(CreateDatabaseEngine),
            _ => throw new ArgumentOutOfRangeException(nameof(lifetime), lifetime, "Invalid service lifetime specified.")
        };

        services.Add(serviceDescriptor);
        return services;
    }

    private static DatabaseEngine CreateDatabaseEngine(IServiceProvider serviceProvider)
    {
        try
        {
            var options = serviceProvider.GetRequiredService<IOptions<DatabaseEngineOptions>>();
            var mpmmf = serviceProvider.GetRequiredService<ManagedPagedMMF>();
            var logger = serviceProvider.GetRequiredService<ILogger<DatabaseEngine>>();

            return new DatabaseEngine(options.Value, mpmmf, logger);
        }
        catch (Exception e)
        {
            Console.WriteLine(e);
            throw;
        }
    }
    
    internal static void EnsureFileDeleted<TO>(this IServiceProvider provider) where TO : PagedMMFOptions
    {
        using var scope = provider.CreateScope();
        var options = scope.ServiceProvider.GetRequiredService<IOptions<TO>>().Value;
        options.EnsureFileDeleted();
    }
}