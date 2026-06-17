using VertexAI.Services;
using VertexAI.Services.Auth;

namespace VertexAI.Api;

public static class AuthEndpoints
{
    public static void MapAuthEndpoints(this WebApplication app)
    {
        var group = app.MapGroup("/api/auth");

        group.MapPost("/login", FirebaseOnlyAsync);
        group.MapPost("/register", FirebaseOnlyAsync);
        group.MapPost("/logout", LogoutAsync);
        group.MapGet("/status", GetStatusAsync);
        group.MapPost("/forgot-password", FirebaseOnlyAsync);
        group.MapPost("/reset-password", FirebaseOnlyAsync);
        group.MapPost("/verify-email", FirebaseOnlyAsync);
        group.MapPost("/resend-verification", FirebaseOnlyAsync);
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

    private static IResult LogoutAsync() =>
        Results.Ok(new AuthResponse(true, null));

    private static IResult FirebaseOnlyAsync() =>
        Results.Json(
            new AuthResponse(false, "本地账号密码认证已停用，请使用 Firebase Authentication。"),
            statusCode: StatusCodes.Status410Gone);
}
