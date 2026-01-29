using JetBrains.Annotations;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using System;

namespace Typhon.Engine;

[PublicAPI]
public static class TyphonServices
{
    public static IServiceProvider ServiceProvider => ServiceProviderInternal ??= CreateDefaultServiceProvider();

    private static ServiceProvider CreateDefaultServiceProvider()
    {
        var serviceCollection = new ServiceCollection();
        serviceCollection
            .AddLogging(builder =>
            {
                builder.AddSimpleConsole(options =>
                {
                    options.SingleLine = true;
                    options.IncludeScopes = true;
                    options.TimestampFormat = "mm:ss.fff ";
                });
                builder.SetMinimumLevel(LogLevel.Information);
            })
            .AddResourceRegistry()
            .AddMemoryAllocator();
        return serviceCollection.BuildServiceProvider();
    }

    public static IMemoryAllocator MemoryAllocator => MemoryAllocatorInternal ??= ServiceProvider.GetService<IMemoryAllocator>();
    public static IResourceRegistry ResourceRegistry => ResourceRegistryInternal ??= ServiceProvider.GetService<IResourceRegistry>();

    public static void Reset()
    {
        ServiceProviderInternal = null;
        MemoryAllocatorInternal = null;
        ResourceRegistryInternal = null;
    }

    public static void Init(IServiceProvider serviceProvider)
    {
        Reset();
        ServiceProviderInternal = serviceProvider;
    }

    private static IServiceProvider ServiceProviderInternal;
    private static IMemoryAllocator MemoryAllocatorInternal;
    private static IResourceRegistry ResourceRegistryInternal;
    
}