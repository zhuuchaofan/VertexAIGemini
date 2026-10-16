using VertexAI.Services.Auth;

namespace VertexAI.Api;

/// <summary>
/// Enforces <see cref="AccessPolicy"/> for every authenticated API call. Anonymous requests are left to
/// the endpoints (they answer 401); only a valid but not-allowed account is answered with 403 here.
/// </summary>
public sealed class AccessPolicyMiddleware
{
    private readonly RequestDelegate _next;

    public AccessPolicyMiddleware(RequestDelegate next)
    {
        _next = next;
    }

    public async Task InvokeAsync(HttpContext context, IUserContext users, AccessPolicy policy)
    {
        if (RequiresPolicy(context.Request.Path))
        {
            var user = await users.GetCurrentUserAsync(context);
            if (user is not null && !policy.IsAllowed(user, out var reason))
            {
                context.Response.StatusCode = StatusCodes.Status403Forbidden;
                await context.Response.WriteAsJsonAsync(new { error = reason ?? "Forbidden" });
                return;
            }
        }

        await _next(context);
    }

    private static bool RequiresPolicy(PathString path) =>
        path.StartsWithSegments("/api")
        && !path.StartsWithSegments("/api/workspace/config");
}
