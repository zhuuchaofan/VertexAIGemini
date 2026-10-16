using FirebaseAdmin.Auth;
using System.Security.Cryptography;
using System.Text;

namespace VertexAI.Services.Auth;

public sealed class FirebaseUserContext : IUserContext
{
    private const string AuthorizationScheme = "Bearer ";

    private readonly FirebaseAuth _auth;
    private readonly ILogger<FirebaseUserContext> _logger;
    private (string Token, AuthenticatedUser? User)? _cached;

    public FirebaseUserContext(
        FirebaseAuth auth,
        ILogger<FirebaseUserContext> logger)
    {
        _auth = auth;
        _logger = logger;
    }

    public async Task<AuthenticatedUser?> GetCurrentUserAsync(HttpContext context)
    {
        var idToken = ReadBearerToken(context);
        if (string.IsNullOrWhiteSpace(idToken))
        {
            return null;
        }

        // The access-policy middleware and the endpoint share one request scope; verify the token once.
        if (_cached is { } cached && string.Equals(cached.Token, idToken, StringComparison.Ordinal))
        {
            return cached.User;
        }

        AuthenticatedUser? user = null;
        try
        {
            var decoded = await _auth.VerifyIdTokenAsync(idToken);
            user = ToAuthenticatedUser(decoded);
        }
        catch (Exception ex) when (ex is FirebaseAuthException or InvalidOperationException)
        {
            _logger.LogWarning(ex, "Firebase token verification failed");
        }

        _cached = (idToken, user);
        return user;
    }

    private AuthenticatedUser? ToAuthenticatedUser(FirebaseToken token)
    {
        if (string.IsNullOrWhiteSpace(token.Uid))
        {
            return null;
        }

        return new AuthenticatedUser(
            CreateStableLocalUserId(token.Uid),
            token.Uid,
            ReadEmail(token),
            ReadAdminClaim(token),
            ReadEmailVerified(token));
    }

    private static string? ReadBearerToken(HttpContext context)
    {
        var authorization = context.Request.Headers.Authorization.FirstOrDefault();
        if (string.IsNullOrWhiteSpace(authorization)
            || !authorization.StartsWith(AuthorizationScheme, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        return authorization[AuthorizationScheme.Length..].Trim();
    }

    private static string? ReadEmail(FirebaseToken token) =>
        token.Claims.TryGetValue("email", out var value)
            ? value as string
            : null;

    private static bool ReadEmailVerified(FirebaseToken token) =>
        token.Claims.TryGetValue("email_verified", out var value)
        && value is bool verified
        && verified;

    private static bool ReadAdminClaim(FirebaseToken token) =>
        token.Claims.TryGetValue("admin", out var value)
        && value is bool admin
        && admin;

    private static Guid CreateStableLocalUserId(string firebaseUid)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes($"firebase:{firebaseUid}"));
        Span<byte> bytes = stackalloc byte[16];
        hash.AsSpan(0, 16).CopyTo(bytes);
        return new Guid(bytes);
    }
}
