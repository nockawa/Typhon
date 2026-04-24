namespace Typhon.Workbench.Sessions;

/// <summary>
/// Per-session handle for a live Typhon app attached over TCP. Owns an <see cref="AttachSessionRuntime"/> that manages
/// the socket + frame-read loop + SSE subscriber fan-out.
/// </summary>
public sealed class AttachSession : ISession, IDisposable
{
    public Guid Id { get; }
    public string EndpointAddress { get; }
    public AttachSessionRuntime Runtime { get; }

    public SessionKind Kind => SessionKind.Attach;
    public SessionState State => SessionState.Attached;

    // ISession.FilePath — DTO compat. For attach sessions the endpoint fills the "where from" slot in the UI.
    public string FilePath => EndpointAddress;

    public AttachSession(Guid id, string endpointAddress, AttachSessionRuntime runtime)
    {
        Id = id;
        EndpointAddress = endpointAddress;
        Runtime = runtime;
    }

    public void Dispose() => Runtime.Dispose();
}
