using VertexAI.Services.Auth;
using VertexAI.Services.Quota;

namespace VertexAI.Api;

public static class QuotaEndpoints
{
    public static void MapQuotaEndpoints(this WebApplication app)
    {
        var group = app.MapGroup("/api/me");

        group.MapGet("/quota", GetQuotaAsync);
    }

    /// <summary>The caller's own daily allowance, so the page can say how much is left. Never reveals anyone else's.</summary>
    internal static async Task<IResult> GetQuotaAsync(
        HttpContext context,
        IUserContext users,
        IQuotaStatusReader quota)
    {
        var user = await users.GetCurrentUserAsync(context);
        if (user is null)
        {
            return Results.Unauthorized();
        }

        return Results.Ok(await quota.GetAsync(user, context.RequestAborted));
    }
}
