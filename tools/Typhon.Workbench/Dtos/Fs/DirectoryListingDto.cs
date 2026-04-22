namespace Typhon.Workbench.Dtos.Fs;

public record DirectoryListingDto(
    string Path,
    string Parent,
    FileEntryDto[] Entries,
    bool Truncated = false);
