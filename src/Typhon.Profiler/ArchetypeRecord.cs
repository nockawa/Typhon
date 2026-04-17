namespace Typhon.Profiler;

/// <summary>
/// Describes an archetype registered with the engine's <c>ArchetypeRegistry</c>. Stored in a table near the start of a <c>.typhon-trace</c> file so
/// the viewer can map <c>ArchetypeId</c> numbers in typed events (<c>EcsSpawn</c>, <c>ClusterMigration</c>, etc.) back to human-readable names
/// without the wire format having to carry strings for every event.
/// </summary>
public sealed class ArchetypeRecord
{
    /// <summary>Archetype ID — matches <c>ArchetypeMetadata.ArchetypeId</c> in the engine.</summary>
    public ushort ArchetypeId { get; init; }

    /// <summary>Display name — typically the archetype class's <c>Type.Name</c>.</summary>
    public string Name { get; init; }
}
