using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using Typhon.Workbench.Sessions;

namespace Typhon.Workbench.Middleware;

[AttributeUsage(AttributeTargets.Class | AttributeTargets.Method)]
public sealed class RequireSessionAttribute : Attribute, IAsyncActionFilter
{
    public async Task OnActionExecutionAsync(ActionExecutingContext context, ActionExecutionDelegate next)
    {
        var sessions = context.HttpContext.RequestServices.GetRequiredService<SessionManager>();

        if (!context.HttpContext.Request.Headers.TryGetValue("X-Session-Token", out var rawToken)
            || !Guid.TryParse(rawToken, out var token)
            || !sessions.TryGet(token, out var session))
        {
            context.Result = new ObjectResult(new ProblemDetails
            {
                Title = "Unauthorized",
                Detail = "Missing or invalid X-Session-Token header.",
                Status = StatusCodes.Status401Unauthorized,
            })
            { StatusCode = StatusCodes.Status401Unauthorized };
            return;
        }

        // When the route carries a {sessionId} segment, require it to match the token's session.
        // Without this check, session A's token could fetch resources scoped to session B.
        if (context.RouteData.Values.TryGetValue("sessionId", out var routeIdRaw)
            && routeIdRaw is string routeIdStr
            && Guid.TryParse(routeIdStr, out var routeId)
            && routeId != session.Id)
        {
            context.Result = new ObjectResult(new ProblemDetails
            {
                Title = "Forbidden",
                Detail = "Session token does not match the session in the URL.",
                Status = StatusCodes.Status403Forbidden,
            })
            { StatusCode = StatusCodes.Status403Forbidden };
            return;
        }

        // Also cover the "/api/sessions/{id}" top-level routes where the param is named "id".
        if (context.RouteData.Values.TryGetValue("id", out var idRaw)
            && idRaw is string idStr
            && Guid.TryParse(idStr, out var id)
            && id != session.Id)
        {
            context.Result = new ObjectResult(new ProblemDetails
            {
                Title = "Forbidden",
                Detail = "Session token does not match the session in the URL.",
                Status = StatusCodes.Status403Forbidden,
            })
            { StatusCode = StatusCodes.Status403Forbidden };
            return;
        }

        context.HttpContext.Items["Session"] = session;
        await next();
    }
}
