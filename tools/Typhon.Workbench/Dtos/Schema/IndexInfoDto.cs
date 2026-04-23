namespace Typhon.Workbench.Dtos.Schema;

/// <summary>
/// One index covering a field of a focused component. Powers the Schema Inspector's Index panel. Offset/size are in
/// bytes within the component storage — same coordinate system as <see cref="FieldDto"/>.
/// </summary>
public record IndexInfoDto(
    string FieldName,
    int FieldOffset,
    int FieldSize,
    bool AllowsMultiple,
    string IndexType);
