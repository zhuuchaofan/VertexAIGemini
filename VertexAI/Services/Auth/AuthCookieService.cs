namespace VertexAI.Services.Auth;

public interface IAuthCookieService
{
    string? ReadSessionToken(HttpContext context);
    void SignIn(HttpContext context, string token);
    void SignOut(HttpContext context);
}

public sealed class AuthCookieService : IAuthCookieService
{
    public const string SessionCookieName = "vertex_auth";
    public const string LegacySessionCookieName = "gemini_auth";
    private static readonly TimeSpan SessionCookieLifetime = TimeSpan.FromDays(7);

    public string? ReadSessionToken(HttpContext context) =>
        context.Request.Cookies[SessionCookieName]
        ?? context.Request.Cookies[LegacySessionCookieName];

    public void SignIn(HttpContext context, string token)
    {
        context.Response.Cookies.Append(SessionCookieName, token, CreateOptions(context));
        context.Response.Cookies.Delete(LegacySessionCookieName);
    }

    public void SignOut(HttpContext context)
    {
        context.Response.Cookies.Delete(SessionCookieName);
        context.Response.Cookies.Delete(LegacySessionCookieName);
    }

    private static CookieOptions CreateOptions(HttpContext context) => new()
    {
        HttpOnly = true,
        Secure = IsHttpsRequest(context),
        SameSite = SameSiteMode.Strict,
        MaxAge = SessionCookieLifetime
    };

    private static bool IsHttpsRequest(HttpContext context)
    {
        if (context.Request.IsHttps)
        {
            return true;
        }

        var forwardedProto = context.Request.Headers["X-Forwarded-Proto"].FirstOrDefault();
        return string.Equals(forwardedProto, "https", StringComparison.OrdinalIgnoreCase);
    }
}
