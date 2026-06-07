using VertexAI.Services;
using VertexAI.Services.Auth;

namespace VertexAI.Api;

public static class AuthEndpoints
{
    public static void MapAuthEndpoints(this WebApplication app)
    {
        var group = app.MapGroup("/api/auth");

        group.MapPost("/login", LoginAsync);
        group.MapPost("/register", RegisterAsync);
        group.MapPost("/logout", LogoutAsync);
        group.MapGet("/status", GetStatusAsync);
        group.MapPost("/forgot-password", ForgotPasswordAsync);
        group.MapPost("/reset-password", ResetPasswordAsync);
        group.MapPost("/verify-email", VerifyEmailAsync);
        group.MapPost("/resend-verification", ResendVerificationAsync);
    }

    private static async Task<IResult> LoginAsync(
        AuthRequest request,
        HttpContext context,
        AuthWorkflowService auth)
    {
        return ToHttpResult(await auth.LoginAsync(request, context));
    }

    private static async Task<IResult> RegisterAsync(
        AuthRequest request,
        HttpContext context,
        AuthWorkflowService auth)
    {
        return ToHttpResult(await auth.RegisterAsync(request, context));
    }

    private static async Task<IResult> LogoutAsync(
        HttpContext context,
        AuthWorkflowService auth)
    {
        return ToHttpResult(await auth.LogoutAsync(context));
    }

    private static async Task<IResult> GetStatusAsync(
        HttpContext context,
        AuthWorkflowService auth)
    {
        return ToHttpResult(await auth.GetStatusAsync(context));
    }

    private static async Task<IResult> ForgotPasswordAsync(
        ForgotPasswordRequest request,
        HttpContext context,
        AuthWorkflowService auth)
    {
        return ToHttpResult(await auth.ForgotPasswordAsync(request, context));
    }

    private static async Task<IResult> ResetPasswordAsync(
        ResetPasswordRequest request,
        HttpContext context,
        AuthWorkflowService auth)
    {
        return ToHttpResult(await auth.ResetPasswordAsync(request, context));
    }

    private static async Task<IResult> VerifyEmailAsync(
        VerifyEmailRequest request,
        HttpContext context,
        AuthWorkflowService auth)
    {
        return ToHttpResult(await auth.VerifyEmailAsync(request, context));
    }

    private static async Task<IResult> ResendVerificationAsync(
        HttpContext context,
        AuthWorkflowService auth)
    {
        return ToHttpResult(await auth.ResendVerificationAsync(context));
    }

    private static IResult ToHttpResult(AuthWorkflowResult result) =>
        result.Status switch
        {
            AuthWorkflowStatus.Ok => Results.Ok(result.Response),
            AuthWorkflowStatus.BadRequest => Results.BadRequest(result.Response),
            AuthWorkflowStatus.Unauthorized => Results.Unauthorized(),
            AuthWorkflowStatus.RateLimited => Results.Json(result.Response, statusCode: 429),
            _ => Results.BadRequest(result.Response)
        };
}
