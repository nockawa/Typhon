using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using Typhon.Workbench.Security;

namespace Typhon.Workbench.Middleware;

/// <summary>
/// Requires a valid <see cref="BootstrapTokenGate.HeaderName"/> header. Applied to every API
/// controller so unauthenticated requests from a browser-origin attacker cannot reach MVC
/// endpoints — see <see cref="BootstrapTokenGate"/> for the threat model.
/// </summary>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Method)]
public sealed class RequireBootstrapTokenAttribute : Attribute, IAsyncActionFilter
{
    public async Task OnActionExecutionAsync(ActionExecutingContext context, ActionExecutionDelegate next)
    {
        var gate = context.HttpContext.RequestServices.GetRequiredService<BootstrapTokenGate>();

        if (!context.HttpContext.Request.Headers.TryGetValue(BootstrapTokenGate.HeaderName, out var raw)
            || !gate.Validate(raw))
        {
            context.Result = new ObjectResult(new ProblemDetails
            {
                Title = "bootstrap_token_required",
                Detail = $"Missing or invalid {BootstrapTokenGate.HeaderName} header.",
                Status = StatusCodes.Status401Unauthorized,
            })
            { StatusCode = StatusCodes.Status401Unauthorized };
            return;
        }

        await next();
    }
}
