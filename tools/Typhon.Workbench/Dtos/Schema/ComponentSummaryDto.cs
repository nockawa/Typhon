namespace Typhon.Workbench.Dtos.Schema;

/// <summary>
/// Triage-friendly summary for the Schema Browser — one row per registered component type. Powers column-based sorting
/// (name, size, field count, entity count, index count) and quick-filter chips ("no entities", "no indexes", "large").
/// </summary>
/// <param name="ArchetypeCount">
/// Number of archetypes containing this component. Nullable — Phase 1 returns null until the ArchetypeRegistry accessor
/// ships in Phase 2 (issue #256). The Browser column is hidden when every row has null.
/// </param>
public record ComponentSummaryDto(
    string TypeName,
    string FullName,
    int StorageSize,
    int FieldCount,
    int? ArchetypeCount,
    int EntityCount,
    int IndexCount);
