namespace Typhon.Workbench.Dtos.Sessions;

public record CreateFileSessionRequest(string FilePath, string[] SchemaDllPaths = null);
