namespace Typhon.Workbench.Dtos.Resources;

/// <summary>
/// SSE payload emitted on the resource graph mutation stream. Minimal — clients invalidate their
/// cached graph and re-fetch; no full subtree is carried on the event.
/// </summary>
public sealed record ResourceGraphEventDto(
    string Kind,
    string NodeId,
    string ParentId,
    string Type,
    DateTimeOffset Timestamp);
