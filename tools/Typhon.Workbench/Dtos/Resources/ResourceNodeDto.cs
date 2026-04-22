namespace Typhon.Workbench.Dtos.Resources;

public record ResourceNodeDto(
    string Id,
    string Name,
    string Type,
    int? EntityCount,
    ResourceNodeDto[] Children);
