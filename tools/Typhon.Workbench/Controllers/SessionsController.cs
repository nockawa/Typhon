using Microsoft.AspNetCore.Mvc;
using Typhon.Workbench.Dtos.Sessions;
using Typhon.Workbench.Middleware;
using Typhon.Workbench.Schema;
using Typhon.Workbench.Sessions;
using WbSession = Typhon.Workbench.Sessions.ISession;

namespace Typhon.Workbench.Controllers;

[ApiController]
[Route("api/sessions")]
[Tags("Sessions")]
[RequireBootstrapToken]
public sealed partial class SessionsController : ControllerBase
{
    private readonly SessionManager _sessions;
    private readonly DemoDataProvider _demoData;
    private readonly ILogger<SessionsController> _logger;

    public SessionsController(SessionManager sessions, DemoDataProvider demoData, ILogger<SessionsController> logger)
    {
        _sessions = sessions;
        _demoData = demoData;
        _logger = logger;
    }

    [HttpPost("file")]
    public async Task<ActionResult<SessionDto>> CreateFileSession([FromBody] CreateFileSessionRequest request, CancellationToken ct)
    {
        // Resolve the file path. The bundled "demo" stem still goes through DemoDataProvider for
        // Phase 3 compat; any other path is used verbatim (Phase 4's real file picker).
        var resolvedFile = ResolveFilePath(request.FilePath);

        // Determine schema DLL list: explicit (user-specified) > convention (adjacent *.schema.dll) > none.
        string[] schemaDllPaths;
        string schemaStatus;
        if (request.SchemaDllPaths is { Length: > 0 })
        {
            schemaDllPaths = request.SchemaDllPaths;
            schemaStatus = "user-specified";
        }
        else
        {
            schemaDllPaths = SchemaConvention.ResolveConventionally(resolvedFile);
            schemaStatus = schemaDllPaths.Length > 0 ? "convention" : "schemaless";
        }

        // Phase 3 compat: single-session at a time per file path.
        _sessions.RemoveWhere(s => s is OpenSession os && string.Equals(os.FilePath, resolvedFile, StringComparison.OrdinalIgnoreCase));

        var engine = await EngineLifecycle.OpenAsync(resolvedFile, schemaDllPaths, ct);

        var sessionState = engine.State switch
        {
            SchemaCompatibility.State.Ready => SessionState.Ready,
            SchemaCompatibility.State.MigrationRequired => SessionState.MigrationRequired,
            SchemaCompatibility.State.Incompatible => SessionState.Incompatible,
            _ => SessionState.Ready,
        };

        var session = new OpenSession(
            Guid.NewGuid(),
            resolvedFile,
            engine,
            sessionState,
            schemaStatus,
            schemaDllPaths,
            engine.LoadedComponentTypes,
            engine.Diagnostics);

        _sessions.Create(session);
        LogSessionCreated(session.Id, "file");
        return CreatedAtAction(nameof(GetSession), new { id = session.Id }, ToDto(session));
    }

    [HttpPost("attach")]
    public ActionResult<SessionDto> CreateAttachSession([FromBody] CreateAttachSessionRequest request)
    {
        var session = new AttachSession(Guid.NewGuid(), request.EndpointAddress);
        _sessions.Create(session);
        LogSessionCreated(session.Id, "attach");
        return CreatedAtAction(nameof(GetSession), new { id = session.Id }, ToDto(session));
    }

    [HttpPost("trace")]
    public ActionResult<SessionDto> CreateTraceSession([FromBody] CreateTraceSessionRequest request)
    {
        var session = new TraceSession(Guid.NewGuid(), request.FilePath);
        _sessions.Create(session);
        LogSessionCreated(session.Id, "trace");
        return CreatedAtAction(nameof(GetSession), new { id = session.Id }, ToDto(session));
    }

    [HttpGet("{id:guid}")]
    [RequireSession]
    public ActionResult<SessionDto> GetSession(Guid id)
    {
        var session = (WbSession)HttpContext.Items["Session"]!;
        return Ok(ToDto(session));
    }

    [HttpDelete("{id:guid}")]
    [RequireSession]
    public IActionResult DeleteSession(Guid id)
    {
        _sessions.Remove(id);
        return NoContent();
    }

    private string ResolveFilePath(string requestPath)
    {
        if (string.IsNullOrWhiteSpace(requestPath))
        {
            throw new WorkbenchException(400, "invalid_path", "filePath is required.");
        }
        // Bundled demo alias: "demo.typhon" → DemoDataProvider path. Any other path is used as-is.
        var stem = Path.GetFileNameWithoutExtension(requestPath);
        if (string.Equals(stem, "demo", StringComparison.OrdinalIgnoreCase)
            && !Path.IsPathRooted(requestPath))
        {
            return _demoData.Resolve(requestPath);
        }
        return Path.GetFullPath(requestPath);
    }

    private static SessionDto ToDto(WbSession s)
    {
        if (s is OpenSession os)
        {
            var diags = os.SchemaDiagnostics?
                .Select(d => new SessionDiagnosticDto(d.ComponentName, d.Kind, d.Detail))
                .ToArray();
            return new SessionDto(
                os.Id,
                os.Kind.ToString(),
                os.State.ToString(),
                os.FilePath,
                os.SchemaDllPaths,
                os.SchemaStatus,
                os.LoadedComponentTypes,
                diags);
        }
        return new SessionDto(s.Id, s.Kind.ToString(), s.State.ToString(), s.FilePath);
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Session {SessionId} created via {Mode}")]
    private partial void LogSessionCreated(Guid sessionId, string mode);
}
