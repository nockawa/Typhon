// unset

using JetBrains.Annotations;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using System;

namespace Typhon.Engine;

/// <summary>
/// Extension methods for registering Typhon telemetry services with dependency injection.
/// </summary>
[PublicAPI]
public static class TelemetryServiceExtensions
{
    /// <summary>
    /// Adds Typhon telemetry configuration to the service collection.
    /// This ensures <see cref="TelemetryConfig"/> is initialized early and
    /// binds <see cref="TelemetryOptions"/> from configuration for DI access.
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <param name="configuration">
    /// Optional configuration root. If provided, <see cref="TelemetryOptions"/> will be
    /// bound from the "Typhon:Telemetry" section. If null, only default options are registered.
    /// </param>
    /// <returns>The service collection for chaining.</returns>
    /// <remarks>
    /// <para>
    /// Call this method early in your application startup, before building the service provider,
    /// to ensure telemetry configuration is loaded before hot paths are JIT compiled.
    /// </para>
    /// <para>
    /// Note: The static <see cref="TelemetryConfig"/> fields are initialized independently
    /// from the DI-bound <see cref="TelemetryOptions"/>. Both use the same configuration sources
    /// but serve different purposes:
    /// <list type="bullet">
    ///   <item><see cref="TelemetryConfig"/>: Static readonly fields for JIT-eliminable hot path checks</item>
    ///   <item><see cref="TelemetryOptions"/>: DI-injectable options for components that need runtime access</item>
    /// </list>
    /// </para>
    /// </remarks>
    /// <example>
    /// <code>
    /// var configuration = new ConfigurationBuilder()
    ///     .AddJsonFile("typhon.telemetry.json", optional: true)
    ///     .AddEnvironmentVariables()
    ///     .Build();
    ///
    /// services.AddTyphonTelemetry(configuration);
    /// </code>
    /// </example>
    public static IServiceCollection AddTyphonTelemetry(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        // Ensure static config is initialized early for JIT optimization
        TelemetryConfig.EnsureInitialized();

        // Bind options from configuration section
        var section = configuration.GetSection(TelemetryOptions.SectionName);
        services.Configure<TelemetryOptions>(options =>
        {
            // Manually bind since we don't have Microsoft.Extensions.Options.ConfigurationExtensions
            options.Enabled = section.GetValue<bool>("Enabled", false);

            var acSection = section.GetSection("AccessControl");
            options.AccessControl.Enabled = acSection.GetValue<bool>("Enabled", false);
            options.AccessControl.TrackContention = acSection.GetValue<bool>("TrackContention", true);
            options.AccessControl.TrackContentionDuration = acSection.GetValue<bool>("TrackContentionDuration", true);
            options.AccessControl.TrackAccessPatterns = acSection.GetValue<bool>("TrackAccessPatterns", true);

            var mmfSection = section.GetSection("PagedMMF");
            options.PagedMMF.Enabled = mmfSection.GetValue<bool>("Enabled", false);
            options.PagedMMF.TrackPageAllocations = mmfSection.GetValue<bool>("TrackPageAllocations", true);
            options.PagedMMF.TrackPageEvictions = mmfSection.GetValue<bool>("TrackPageEvictions", true);
            options.PagedMMF.TrackIOOperations = mmfSection.GetValue<bool>("TrackIOOperations", true);
            options.PagedMMF.TrackCacheHitRatio = mmfSection.GetValue<bool>("TrackCacheHitRatio", true);

            var btreeSection = section.GetSection("BTree");
            options.BTree.Enabled = btreeSection.GetValue<bool>("Enabled", false);
            options.BTree.TrackNodeSplits = btreeSection.GetValue<bool>("TrackNodeSplits", true);
            options.BTree.TrackNodeMerges = btreeSection.GetValue<bool>("TrackNodeMerges", true);
            options.BTree.TrackSearchDepth = btreeSection.GetValue<bool>("TrackSearchDepth", true);
            options.BTree.TrackKeyComparisons = btreeSection.GetValue<bool>("TrackKeyComparisons", false);

            var txSection = section.GetSection("Transaction");
            options.Transaction.Enabled = txSection.GetValue<bool>("Enabled", false);
            options.Transaction.TrackCommitRollback = txSection.GetValue<bool>("TrackCommitRollback", true);
            options.Transaction.TrackConflicts = txSection.GetValue<bool>("TrackConflicts", true);
            options.Transaction.TrackDuration = txSection.GetValue<bool>("TrackDuration", true);
        });

        return services;
    }

    /// <summary>
    /// Adds Typhon telemetry configuration with default options.
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <returns>The service collection for chaining.</returns>
    public static IServiceCollection AddTyphonTelemetry(this IServiceCollection services)
    {
        // Ensure static config is initialized early for JIT optimization
        TelemetryConfig.EnsureInitialized();

        // Register default options
        services.Configure<TelemetryOptions>(_ => { });

        return services;
    }

    /// <summary>
    /// Adds Typhon telemetry configuration with a configuration action.
    /// Use this overload to programmatically configure telemetry without a config file.
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <param name="configure">Action to configure telemetry options.</param>
    /// <returns>The service collection for chaining.</returns>
    /// <remarks>
    /// <para>
    /// <b>Important:</b> Options configured via this method only affect DI-resolved
    /// <see cref="TelemetryOptions"/>. The static <see cref="TelemetryConfig"/> fields
    /// are initialized from files/environment variables in the static constructor
    /// and cannot be changed programmatically.
    /// </para>
    /// <para>
    /// For hot path telemetry controlled by <see cref="TelemetryConfig"/>, use
    /// environment variables or a configuration file.
    /// </para>
    /// </remarks>
    /// <example>
    /// <code>
    /// services.AddTyphonTelemetry(options =>
    /// {
    ///     options.Enabled = true;
    ///     options.AccessControl.Enabled = true;
    ///     options.AccessControl.TrackContention = true;
    /// });
    /// </code>
    /// </example>
    public static IServiceCollection AddTyphonTelemetry(
        this IServiceCollection services,
        Action<TelemetryOptions> configure)
    {
        // Ensure static config is initialized early for JIT optimization
        TelemetryConfig.EnsureInitialized();

        // Register options with the configure action
        services.Configure(configure);

        return services;
    }
}
