namespace Typhon.Workbench.Dtos.Sessions;

public record SessionDto(
    Guid SessionId,
    string Kind,
    string State,
    string FilePath,
    string[] SchemaDllPaths = null,
    string SchemaStatus = null,
    int LoadedComponentTypes = 0,
    SessionDiagnosticDto[] SchemaDiagnostics = null);

public record SessionDiagnosticDto(string ComponentName, string Kind, string Detail);
