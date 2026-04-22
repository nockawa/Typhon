using Microsoft.AspNetCore.Mvc;

namespace Typhon.Workbench.Controllers;

[ApiController]
[Tags("Health")]
public sealed class HealthController : ControllerBase
{
    [HttpGet("/health")]
    public IActionResult GetHealth() => Ok(new { status = "ok", phase = 2 });
}
