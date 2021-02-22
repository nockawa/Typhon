using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using System;
using System.Collections.Generic;

namespace Typhon.Engine
{
    public static class TyphonBuilderExtensions
    {
        public static ITyphonBuilder AddProvider(this ITyphonBuilder builder, IConfigurationProvider<DatabaseConfiguration> provider) => ((ITyphonBuilderImplementation)builder).ConfigureServices(services => services.AddSingleton(provider));
        public static ITyphonBuilder ConfigureDatabase(this ITyphonBuilder builder, Action<DatabaseConfiguration> configure) => builder.AddProvider(new DelegateConfigurationProvider<DatabaseConfiguration>(configure));

        private sealed class DelegateConfigurationProvider<TOptions> : IConfigurationProvider<TOptions>
        {
            private readonly Action<TOptions> _configure;

            public DelegateConfigurationProvider(Action<TOptions> configure)
            {
                _configure = configure;
            }

            public void Configure(TOptions configuration) => _configure(configuration);
        }
    }

    /// <summary>
    /// Holds configuration of the specified type.
    /// </summary>
    /// <typeparam name="TConfiguration">The configuration  type.</typeparam>
    // ReSharper disable once TypeParameterCanBeVariant
    public interface IConfiguration<TConfiguration> where TConfiguration : class, new()
    {
        /// <summary>
        /// Gets the configuration value.
        /// </summary>
        TConfiguration Value { get; }
    }

    /// <inheritdoc />
    internal class ConfigurationHolder<TConfiguration> : IConfiguration<TConfiguration> where TConfiguration : class, new()
    {
        /// <inheritdoc />
        public ConfigurationHolder(IEnumerable<IConfigurationProvider<TConfiguration>> providers)
        {
            Value = new TConfiguration();
            foreach (var provider in providers)
            {
                provider.Configure(Value);
            }
        }

        /// <inheritdoc />
        public TConfiguration Value { get; }
    }

    public interface ITyphonBuilder
    {

    }

    public interface ITyphonBuilderImplementation : ITyphonBuilder
    {
        ITyphonBuilderImplementation ConfigureServices(Action<IServiceCollection> configureDelegate);
    }

    public static class ServiceProviderExtensions
    {
        public static IServiceCollection AddTyphon(this IServiceCollection services, Action<ITyphonBuilder> configure = null)
        {
            // Only add the services once.
            var context = GetFromServices<TyphonConfigurationContext>(services);
            if (context is null)
            {
                context = new TyphonConfigurationContext(services);
                services.Add(context.CreateServiceDescriptor());

                services.AddSingleton<IConfigurationProvider<DatabaseConfiguration>, DefaultDatabaseConfiguration>();
                services.AddSingleton<VirtualDiskManager>();
                services.AddSingleton<PersistentDataAccess>();
                services.AddSingleton<TimeManager>();
                services.TryAddSingleton(typeof(IConfiguration<>), typeof(ConfigurationHolder<>));
                services.TryAddSingleton<DatabaseEngine>();

            }

            configure?.Invoke(context.Builder);

            return services;
        }

        private static T GetFromServices<T>(IServiceCollection services)
        {
            foreach (var service in services)
            {
                if (service.ServiceType == typeof(T))
                {
                    return (T)service.ImplementationInstance;
                }
            }

            return default;
        }

        private sealed class TyphonConfigurationContext
        {
            public TyphonConfigurationContext(IServiceCollection services) => Builder = new TyphonBuilder(services);

            public ServiceDescriptor CreateServiceDescriptor() => new ServiceDescriptor(typeof(TyphonConfigurationContext), this);

            public ITyphonBuilder Builder { get; }
        }

        private class TyphonBuilder : ITyphonBuilderImplementation
        {
            private readonly IServiceCollection _services;

            public TyphonBuilder(IServiceCollection services) => _services = services;

            public ITyphonBuilderImplementation ConfigureServices(Action<IServiceCollection> configureDelegate)
            {
                configureDelegate(_services);
                return this;
            }
        }
    }
}
