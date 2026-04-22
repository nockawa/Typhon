namespace Typhon.Workbench.Dtos.Fs;

public record FileEntryDto(
    string Name,
    string FullPath,
    string Kind,
    long? Size,
    DateTime? LastWriteUtc,
    bool IsSchemaDll);
