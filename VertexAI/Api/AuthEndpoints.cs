using VertexAI.Services.Auth;

namespace VertexAI.Api;

public static class AuthEndpoints
{
    public static void MapAuthEndpoints(this WebApplication app)
    {
        var group = app.MapGroup("/api/auth");

        group.MapGet("/status", GetStatusAsync);
    }

    private static async Task<IResult> GetStatusAsync(
        HttpContext context,
        IUserContext users)
    {
        var user = await users.GetCurrentUserAsync(context);
        if (user is null)
        {
            return Results.Unauthorized();
        }

        return Results.Ok(new AuthResponse(
            true,
            null,
            new UserInfo(user.LocalUserId, user.Email ?? "Firebase user", EmailVerified: true)));
    }

    private sealed record AuthResponse(bool Success, string? Error, UserInfo? User = null);

    private sealed record UserInfo(Guid Id, string Email, bool EmailVerified = false);
}
