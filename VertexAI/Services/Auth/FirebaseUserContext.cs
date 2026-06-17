using FirebaseAdmin;
using FirebaseAdmin.Auth;
using Google.Apis.Auth.OAuth2;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using VertexAI.Configuration;
using VertexAI.Data;
using VertexAI.Data.Entities;

namespace VertexAI.Services.Auth;

public sealed class FirebaseUserContext : IUserContext
{
    private const string AuthorizationScheme = "Bearer ";

    private readonly IDbContextFactory<AppDbContext> _dbFactory;
    private readonly SessionUserContext _fallback;
    private readonly FirebaseSettings _settings;
    private readonly ILogger<FirebaseUserContext> _logger;

    public FirebaseUserContext(
        IDbContextFactory<AppDbContext> dbFactory,
        SessionUserContext fallback,
        IOptions<FirebaseSettings> settings,
        ILogger<FirebaseUserContext> logger)
    {
        _dbFactory = dbFactory;
        _fallback = fallback;
        _settings = settings.Value;
        _logger = logger;
    }

    public async Task<Guid?> GetCurrentUserIdAsync(HttpContext context)
    {
        var idToken = ReadBearerToken(context);
        if (string.IsNullOrWhiteSpace(idToken))
        {
            return await _fallback.GetCurrentUserIdAsync(context);
        }

        try
        {
            var decoded = await GetFirebaseAuth().VerifyIdTokenAsync(idToken);
            return await GetOrCreateLocalUserAsync(decoded);
        }
        catch (Exception ex) when (ex is FirebaseAuthException or InvalidOperationException)
        {
            _logger.LogWarning(ex, "Firebase token verification failed");
            return null;
        }
    }

    private async Task<Guid?> GetOrCreateLocalUserAsync(FirebaseToken token)
    {
        if (string.IsNullOrWhiteSpace(token.Uid))
        {
            return null;
        }

        await using var db = await _dbFactory.CreateDbContextAsync();
        var user = await db.Users.FirstOrDefaultAsync(u => u.FirebaseUid == token.Uid);
        if (user != null)
        {
            user.LastLoginAt = DateTime.UtcNow;
            await db.SaveChangesAsync();
            return user.Id;
        }

        var email = ReadEmail(token);
        if (string.IsNullOrWhiteSpace(email))
        {
            _logger.LogWarning("Firebase token did not include an email, Uid={Uid}", token.Uid);
            return null;
        }

        email = AuthInputValidator.NormalizeEmail(email);
        user = await db.Users.FirstOrDefaultAsync(u => u.Email == email);
        if (user == null)
        {
            user = new User
            {
                Email = email,
                FirebaseUid = token.Uid,
                PasswordHash = BCrypt.Net.BCrypt.HashPassword(Guid.NewGuid().ToString("N")),
                EmailVerified = ReadEmailVerified(token),
                LastLoginAt = DateTime.UtcNow
            };
            db.Users.Add(user);
        }
        else
        {
            user.FirebaseUid = token.Uid;
            user.EmailVerified = user.EmailVerified || ReadEmailVerified(token);
            user.LastLoginAt = DateTime.UtcNow;
        }

        await db.SaveChangesAsync();
        return user.Id;
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

    private static bool ReadEmailVerified(FirebaseToken token) =>
        token.Claims.TryGetValue("email_verified", out var value)
        && value is bool verified
        && verified;
}
