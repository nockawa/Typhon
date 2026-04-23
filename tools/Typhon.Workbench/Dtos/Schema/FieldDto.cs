namespace Typhon.Workbench.Dtos.Schema;

/// <summary>
/// One field within a component's byte layout. Offsets and sizes are in bytes within the component storage
/// (excluding the engine-managed EntityPK overhead — see <see cref="ComponentSchemaDto"/>).
/// </summary>
public record FieldDto(
    string Name,
    string TypeName,
    string TypeFullName,
    int Offset,
    int Size,
    int FieldId,
    bool IsIndexed,
    bool IndexAllowsMultiple);
