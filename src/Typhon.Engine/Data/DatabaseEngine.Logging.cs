using Microsoft.Extensions.Logging;

namespace Typhon.Engine;

/// <summary>
/// Source-generated log messages for <see cref="DatabaseEngine"/>. Using <c>[LoggerMessage]</c> keeps
/// level checks in the generated wrapper (no boxing or allocation when the level is filtered out),
/// per CLAUDE.md guidance.
/// </summary>
public partial class DatabaseEngine
{
    [LoggerMessage(Level = LogLevel.Debug,
        Message = "Spatial archetype '{ArchetypeName}' has no SpatialGrid configured — falling back to legacy per-entity placement. " +
                  "Call DatabaseEngine.ConfigureSpatialGrid(...) before InitializeArchetypes() to enable cell-coherent clustering.")]
    private partial void LogSpatialArchetypeNoGridFallback(string archetypeName);
}
