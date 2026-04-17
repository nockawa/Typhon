namespace Typhon.Profiler;

/// <summary>
/// Describes a component type registered with the engine. Stored in a table near the start of a <c>.typhon-trace</c> file so the viewer can map
/// <c>ComponentTypeId</c> numbers in typed events (e.g. <c>TransactionCommitComponent</c>) back to human-readable names without the wire format
/// carrying strings per event.
/// </summary>
public sealed class ComponentTypeRecord
{
    /// <summary>Component type ID — the index the engine assigns during <c>RegisterComponentFromAccessor</c>.</summary>
    public int ComponentTypeId { get; init; }

    /// <summary>Display name — the C# type's full name.</summary>
    public string Name { get; init; }
}
