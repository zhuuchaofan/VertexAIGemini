using FirebaseAdmin;
using FirebaseAdmin.Auth;
using Google.Apis.Auth.OAuth2;
using Microsoft.Extensions.Options;
using System.Security.Cryptography;
using System.Text;
using VertexAI.Configuration;

namespace VertexAI.Services.Auth;

public sealed class FirebaseUserContext : IUserContext
{
    private const string AuthorizationScheme = "Bearer ";

    private readonly FirebaseSettings _settings;
    private readonly ILogger<FirebaseUserContext> _logger;

    public FirebaseUserContext(
        IOptions<FirebaseSettings> settings,
        ILogger<FirebaseUserContext> logger)
    {
        _settings = settings.Value;
        _logger = logger;
    }

    public async Task<AuthenticatedUser?> GetCurrentUserAsync(HttpContext context)
    {
        var idToken = ReadBearerToken(context);
        if (string.IsNullOrWhiteSpace(idToken))
        {
            return null;
        }

        try
        {
            var decoded = await GetFirebaseAuth().VerifyIdTokenAsync(idToken);
            return ToAuthenticatedUser(decoded);
        }
        catch (Exception ex) when (ex is FirebaseAuthException or InvalidOperationException)
        {
            _logger.LogWarning(ex, "Firebase token verification failed");
            return null;
        }
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
            ReadEmail(token));
    }

    private FirebaseAuth GetFirebaseAuth()
    {
        var app = FirebaseApp.DefaultInstance ?? FirebaseApp.Create(new AppOptions
        {
            Credential = GoogleCredential.GetApplicationDefault(),
            ProjectId = ResolveProjectId()
        });

        return FirebaseAuth.GetAuth(app);
    }

    private string? ResolveProjectId() =>
        Environment.GetEnvironmentVariable("FIREBASE_PROJECT_ID")
        ?? _settings.ProjectId
        ?? Environment.GetEnvironmentVariable("GOOGLE_CLOUD_PROJECT")
        ?? Environment.GetEnvironmentVariable("PROJECT_ID");

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

    private static Guid CreateStableLocalUserId(string firebaseUid)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes($"firebase:{firebaseUid}"));
        Span<byte> bytes = stackalloc byte[16];
        hash.AsSpan(0, 16).CopyTo(bytes);
        return new Guid(bytes);
    }
}
