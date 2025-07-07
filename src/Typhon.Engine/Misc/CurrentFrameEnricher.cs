// unset

using Serilog;
using Serilog.Configuration;
using Serilog.Core;
using Serilog.Events;
using System;

namespace Typhon.Engine;

public static class LogExtensions
{
    public static LoggerConfiguration WithCurrentFrame(this LoggerEnrichmentConfiguration enrichmentConfiguration) =>
        enrichmentConfiguration != null ? enrichmentConfiguration.With<CurrentFrameEnricher>() : throw new ArgumentNullException(nameof(enrichmentConfiguration));
}

public class CurrentFrameEnricher : ILogEventEnricher
{
    /// <summary>The property name added to enriched log events.</summary>
    public const string ThreadIdPropertyName = "ThreadId";

    /// <summary>Enrich the log event.</summary>
    /// <param name="logEvent">The log event to enrich.</param>
    /// <param name="propertyFactory">Factory for creating new properties to add to the event.</param>
    public void Enrich(LogEvent logEvent, ILogEventPropertyFactory propertyFactory) => logEvent.AddPropertyIfAbsent(new LogEventProperty("CurrentFrame", (LogEventPropertyValue) new ScalarValue((object) TimeManager.Singleton.ExecutionFrame)));
}