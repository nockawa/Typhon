using System.Collections.Concurrent;
using WbSession = Typhon.Workbench.Sessions.ISession;

namespace Typhon.Workbench.Sessions;

public sealed partial class SessionManager
{
    private readonly ConcurrentDictionary<Guid, WbSession> _sessions = new();
    private readonly ILogger<SessionManager> _logger;

    public SessionManager(ILogger<SessionManager> logger) => _logger = logger;

    public WbSession Create(WbSession session)
    {
        _sessions[session.Id] = session;
        LogSessionCreated(session.Id, session.Kind);
        return session;
    }

    public bool TryGet(Guid id, out WbSession session)
    {
        var found = _sessions.TryGetValue(id, out var s);
        session = s;
        return found;
    }

    public bool Remove(Guid id)
    {
        var removed = _sessions.TryRemove(id, out var session);
        if (!removed) return false;

        if (session is IDisposable d)
        {
            try { d.Dispose(); }
            catch (Exception ex) { LogSessionDisposeFailed(id, ex.Message); }
        }

        LogSessionRemoved(id, session.Kind);
        return true;
    }

    public void DisposeAll()
    {
        foreach (var key in _sessions.Keys.ToArray())
        {
            Remove(key);
        }
    }

    /// <summary>
    /// Removes any existing sessions matching the predicate. Used by the single-session Open flow
    /// to guarantee a prior session's file handles are released before opening the same path anew.
    /// </summary>
    public int RemoveWhere(Func<WbSession, bool> predicate)
    {
        var count = 0;
        foreach (var kv in _sessions.ToArray())
        {
            if (predicate(kv.Value))
            {
                if (Remove(kv.Key)) count++;
            }
        }
        return count;
    }

    public int Count => _sessions.Count;

    [LoggerMessage(Level = LogLevel.Information, Message = "Session {SessionId} created (kind: {Kind})")]
    private partial void LogSessionCreated(Guid sessionId, SessionKind kind);

    [LoggerMessage(Level = LogLevel.Information, Message = "Session {SessionId} removed (kind: {Kind})")]
    private partial void LogSessionRemoved(Guid sessionId, SessionKind kind);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Session {SessionId} disposal failed: {Error}")]
    private partial void LogSessionDisposeFailed(Guid sessionId, string error);
}
