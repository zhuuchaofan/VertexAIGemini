using VertexAI.Services.Auth;
using VertexAI.Services;

namespace VertexAI.Api;

public static class AuthEndpoints
{
    public static void MapAuthEndpoints(this WebApplication app)
    {
        var group = app.MapGroup("/api/auth");

        group.MapGet("/status", GetStatusAsync);
    }

    internal static async Task<IResult> GetStatusAsync(
        HttpContext context,
        IUserContext users,
        GeminiSafetyPolicy? safety = null)
    {
        var user = await users.GetCurrentUserAsync(context);
        if (user is null)
        {
            return Results.Unauthorized();
        }

        return Results.Ok(new AuthResponse(
            true,
            null,
            new UserInfo(user.LocalUserId, user.Email ?? "Firebase user", EmailVerified: true),
            safety?.GetAllowedThresholds(user) ?? []));
    }

    private sealed record AuthResponse(bool Success, string? Error, UserInfo? User = null,
        IReadOnlyList<string>? SafetyThresholds = null);

    private sealed record UserInfo(Guid Id, string Email, bool EmailVerified = false);
}
