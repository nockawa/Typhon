namespace Typhon.Workbench.Dtos.Schema;

/// <summary>
/// One system that reads or reacts to the focused component. This DTO is reads-only for legacy compatibility — full system
/// access (writes, fresh/snapshot reads, side-writes, events, named resources) is now tracked via
/// <c>SystemAccessDescriptor</c> (RFC 07 — Unit 2 in the engine layer). Surfacing it through Workbench requires
/// <c>OpenSession</c> to host a live <c>TyphonRuntime</c>, tracked in issue #275 (Unit 6). Once that lands, extend this record
/// or create a sibling <c>SystemAccessDto</c> exposing the full access shape.
/// </summary>
/// <param name="Access">
/// <c>"read"</c> — component is in the system's input view schema (implicit read set via query filter).
/// <c>"reactive"</c> — component is in the system's <c>ChangeFilterTypes</c> (reactive trigger).
/// A single system may appear twice (once per access kind) if it both reads and reactively triggers on the component.
/// </param>
/// <param name="QueryViewSchema">Component types in the system's input view, or empty for callback systems.</param>
/// <param name="ChangeFilterTypes">Reactive-trigger component types, or empty if the system has none.</param>
public record SystemRelationshipDto(
    string SystemName,
    string SystemType,
    string Access,
    string[] QueryViewSchema,
    string[] ChangeFilterTypes);

/// <summary>
/// Envelope for the system-relationships endpoint. The <see cref="RuntimeHosted"/> flag lets the client distinguish
/// "this component has no system relationships" (hosted + empty) from "the Workbench does not host a runtime yet"
/// (not hosted — a different empty-state banner).
/// </summary>
public record SystemRelationshipsResponseDto(
    bool RuntimeHosted,
    SystemRelationshipDto[] Systems);
