namespace Typhon.Workbench.Sessions;

public interface ISession
{
    Guid Id { get; }
    SessionKind Kind { get; }
    SessionState State { get; }
    string FilePath { get; }
}
