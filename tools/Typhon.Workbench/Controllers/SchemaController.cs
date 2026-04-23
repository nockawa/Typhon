using Microsoft.AspNetCore.Mvc;
using Typhon.Workbench.Dtos.Schema;
using Typhon.Workbench.Middleware;
using Typhon.Workbench.Schema;

namespace Typhon.Workbench.Controllers;

[ApiController]
[Route("api/sessions/{sessionId:guid}/schema")]
[Tags("Schema")]
[RequireBootstrapToken]
[RequireSession]
public sealed class SchemaController : ControllerBase
{
    private readonly SchemaService _schema;

    public SchemaController(SchemaService schema)
    {
        _schema = schema;
    }

    [HttpGet("components")]
    public ActionResult<ComponentSummaryDto[]> ListComponents(Guid sessionId) => Invoke(() => _schema.ListComponents(sessionId));

    [HttpGet("components/{typeName}")]
    public ActionResult<ComponentSchemaDto> GetComponentSchema(Guid sessionId, string typeName) => Invoke(() => _schema.GetComponentSchema(sessionId, typeName));

    [HttpGet("archetypes")]
    public ActionResult<ArchetypeInfoDto[]> ListArchetypes(Guid sessionId) => Invoke(() => _schema.ListArchetypes(sessionId));

    [HttpGet("components/{typeName}/archetypes")]
    public ActionResult<ArchetypeInfoDto[]> GetArchetypes(Guid sessionId, string typeName) => Invoke(() => _schema.GetArchetypesForComponent(sessionId, typeName));

    [HttpGet("components/{typeName}/indexes")]
    public ActionResult<IndexInfoDto[]> GetIndexes(Guid sessionId, string typeName) => Invoke(() => _schema.GetIndexesForComponent(sessionId, typeName));

    [HttpGet("components/{typeName}/systems")]
    public ActionResult<SystemRelationshipsResponseDto> GetSystems(Guid sessionId, string typeName) => Invoke(() => _schema.GetSystemRelationships(sessionId, typeName));

    private ActionResult<T> Invoke<T>(Func<T> action)
    {
        try
        {
            return Ok(action());
        }
        catch (SessionKindException ex)
        {
            return Conflict(new ProblemDetails
            {
                Title = "session_kind_mismatch",
                Detail = ex.Message,
                Status = StatusCodes.Status409Conflict,
            });
        }
        catch (SessionNotFoundException)
        {
            return NotFound();
        }
        catch (KeyNotFoundException)
        {
            return NotFound();
        }
    }
}
