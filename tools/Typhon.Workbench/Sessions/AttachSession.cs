namespace Typhon.Workbench.Sessions;

public record AttachSession(Guid Id, string EndpointAddress) : ISession
{
    public SessionKind Kind => SessionKind.Attach;
    public SessionState State => SessionState.Attached;
    public string FilePath => string.Empty;
}
