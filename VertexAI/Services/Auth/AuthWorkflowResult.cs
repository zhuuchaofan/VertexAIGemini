namespace VertexAI.Services.Auth;

public enum AuthWorkflowStatus
{
    Ok,
    BadRequest,
    Unauthorized,
    RateLimited
}

public sealed record AuthWorkflowResult(
    AuthWorkflowStatus Status,
    AuthResponse Response)
{
    public static AuthWorkflowResult Ok(string? message = null, UserInfo? user = null) =>
        new(AuthWorkflowStatus.Ok, new AuthResponse(true, message, user));

    public static AuthWorkflowResult BadRequest(string message) =>
        new(AuthWorkflowStatus.BadRequest, new AuthResponse(false, message));

    public static AuthWorkflowResult Unauthorized() =>
        new(AuthWorkflowStatus.Unauthorized, new AuthResponse(false, null));

    public static AuthWorkflowResult RateLimited() =>
        new(AuthWorkflowStatus.RateLimited, new AuthResponse(false, "\u64cd\u4f5c\u8fc7\u4e8e\u9891\u7e41\uff0c\u8bf7\u7a0d\u540e\u518d\u8bd5"));
}
