namespace Typhon.Workbench.Sessions;

public record TraceSession(Guid Id, string FilePath) : ISession
{
    public SessionKind Kind => SessionKind.Trace;
    public SessionState State => SessionState.Trace;
}
