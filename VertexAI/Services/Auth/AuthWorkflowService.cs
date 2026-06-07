using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using VertexAI.Data;
using VertexAI.Data.Entities;

namespace VertexAI.Services.Auth;

public class AuthWorkflowService
{
    private readonly IDbContextFactory<AppDbContext> _dbFactory;
    private readonly EmailService _email;
    private readonly IAuthRateLimiter _rateLimiter;
    private readonly IAuthCookieService _cookies;
    private readonly IAuthSessionStore _sessions;
    private readonly IAuthTokenGenerator _tokens;
    private readonly ILogger<AuthWorkflowService> _logger;

    public AuthWorkflowService(
        IDbContextFactory<AppDbContext> dbFactory,
        EmailService email,
        IAuthRateLimiter rateLimiter,
        IAuthCookieService cookies,
        IAuthSessionStore sessions,
        IAuthTokenGenerator tokens,
        ILogger<AuthWorkflowService> logger)
    {
        _dbFactory = dbFactory;
        _email = email;
        _rateLimiter = rateLimiter;
        _cookies = cookies;
        _sessions = sessions;
        _tokens = tokens;
        _logger = logger;
    }

    public async Task<AuthWorkflowResult> LoginAsync(AuthRequest request, HttpContext context)
    {
        if (_rateLimiter.IsLimited(context))
        {
            return AuthWorkflowResult.RateLimited();
        }

        if (string.IsNullOrWhiteSpace(request.Email) || string.IsNullOrWhiteSpace(request.Password))
        {
            return AuthWorkflowResult.BadRequest("\u90ae\u7bb1\u548c\u5bc6\u7801\u4e0d\u80fd\u4e3a\u7a7a");
        }

        await using var db = await _dbFactory.CreateDbContextAsync();
        var email = AuthInputValidator.NormalizeEmail(request.Email);
        var user = await db.Users.FirstOrDefaultAsync(u => u.Email == email);

        if (user == null || !BCrypt.Net.BCrypt.Verify(request.Password, user.PasswordHash))
        {
            _rateLimiter.RecordFailure(context);
            return AuthWorkflowResult.BadRequest("\u90ae\u7bb1\u6216\u5bc6\u7801\u9519\u8bef");
        }

        user.LastLoginAt = DateTime.UtcNow;
        await _sessions.ClearExpiredSessionsAsync(db, user.Id);
        var session = await _sessions.CreateSessionAsync(db, user.Id);

        _cookies.SignIn(context, session.Token);
        _rateLimiter.Reset(context);

        return AuthWorkflowResult.Ok(user: ToUserInfo(user));
    }

    public async Task<AuthWorkflowResult> RegisterAsync(AuthRequest request, HttpContext context)
    {
        if (_rateLimiter.IsLimited(context))
        {
            return AuthWorkflowResult.RateLimited();
        }

        if (string.IsNullOrWhiteSpace(request.Email) || string.IsNullOrWhiteSpace(request.Password))
        {
            return AuthWorkflowResult.BadRequest("\u90ae\u7bb1\u548c\u5bc6\u7801\u4e0d\u80fd\u4e3a\u7a7a");
        }

        var passwordError = AuthInputValidator.ValidatePasswordStrength(request.Password);
        if (passwordError != null)
        {
            return AuthWorkflowResult.BadRequest(passwordError);
        }

        var email = AuthInputValidator.NormalizeEmail(request.Email);
        if (!AuthInputValidator.IsValidEmail(email))
        {
            return AuthWorkflowResult.BadRequest("\u8bf7\u8f93\u5165\u6709\u6548\u7684\u90ae\u7bb1\u5730\u5740");
        }

        await using var db = await _dbFactory.CreateDbContextAsync();

        if (await db.Users.AnyAsync(u => u.Email == email))
        {
            _rateLimiter.RecordFailure(context);
            return AuthWorkflowResult.BadRequest("\u8be5\u90ae\u7bb1\u5df2\u88ab\u6ce8\u518c");
        }

        var user = new User
        {
            Email = email,
            PasswordHash = BCrypt.Net.BCrypt.HashPassword(request.Password),
            EmailVerified = false,
            VerificationToken = _tokens.Generate()
        };

        db.Users.Add(user);
        await db.SaveChangesAsync();

        var session = await _sessions.CreateSessionAsync(db, user.Id);
        _cookies.SignIn(context, session.Token);
        _rateLimiter.Reset(context);

        SendVerificationEmailInBackground(user.Email, user.VerificationToken);

        return AuthWorkflowResult.Ok(user: ToUserInfo(user));
    }

    public async Task<AuthWorkflowResult> LogoutAsync(HttpContext context)
    {
        var token = _cookies.ReadSessionToken(context);
        if (!string.IsNullOrEmpty(token))
        {
            await _sessions.DeleteSessionAsync(token);
        }

        _cookies.SignOut(context);
        return AuthWorkflowResult.Ok("\u5df2\u767b\u51fa");
    }

    public async Task<AuthWorkflowResult> GetStatusAsync(HttpContext context)
    {
        var token = _cookies.ReadSessionToken(context);
        if (string.IsNullOrEmpty(token))
        {
            return new AuthWorkflowResult(AuthWorkflowStatus.Ok, new AuthResponse(false, null));
        }

        var user = await _sessions.GetUserBySessionTokenAsync(token);
        if (user == null)
        {
            _cookies.SignOut(context);
            return new AuthWorkflowResult(AuthWorkflowStatus.Ok, new AuthResponse(false, null));
        }

        return AuthWorkflowResult.Ok(user: ToUserInfo(user));
    }

    public async Task<AuthWorkflowResult> ForgotPasswordAsync(ForgotPasswordRequest request, HttpContext context)
    {
        if (_rateLimiter.IsLimited(context))
        {
            return AuthWorkflowResult.RateLimited();
        }

        if (string.IsNullOrWhiteSpace(request.Email))
        {
            return AuthWorkflowResult.BadRequest("\u8bf7\u8f93\u5165\u90ae\u7bb1\u5730\u5740");
        }

        var email = AuthInputValidator.NormalizeEmail(request.Email);
        await using var db = await _dbFactory.CreateDbContextAsync();
        var user = await db.Users.FirstOrDefaultAsync(u => u.Email == email);

        if (user != null)
        {
            user.PasswordResetToken = _tokens.Generate();
            user.PasswordResetExpiresAt = DateTime.UtcNow.AddHours(1);
            await db.SaveChangesAsync();

            _logger.LogInformation("Password reset token generated, Email={Email}", email);
            SendPasswordResetEmailInBackground(user.Email, user.PasswordResetToken);
        }

        _rateLimiter.RecordFailure(context);
        return AuthWorkflowResult.Ok("\u5982\u679c\u8be5\u90ae\u7bb1\u5df2\u6ce8\u518c\uff0c\u91cd\u7f6e\u94fe\u63a5\u5c06\u53d1\u9001\u5230\u60a8\u7684\u90ae\u7bb1");
    }

    public async Task<AuthWorkflowResult> ResetPasswordAsync(ResetPasswordRequest request, HttpContext context)
    {
        if (_rateLimiter.IsLimited(context))
        {
            return AuthWorkflowResult.RateLimited();
        }

        if (string.IsNullOrWhiteSpace(request.Token) || string.IsNullOrWhiteSpace(request.NewPassword))
        {
            return AuthWorkflowResult.BadRequest("Token \u548c\u65b0\u5bc6\u7801\u4e0d\u80fd\u4e3a\u7a7a");
        }

        var passwordError = AuthInputValidator.ValidatePasswordStrength(request.NewPassword);
        if (passwordError != null)
        {
            return AuthWorkflowResult.BadRequest(passwordError);
        }

        await using var db = await _dbFactory.CreateDbContextAsync();
        var user = await db.Users.FirstOrDefaultAsync(u =>
            u.PasswordResetToken == request.Token &&
            u.PasswordResetExpiresAt > DateTime.UtcNow);

        if (user == null)
        {
            _rateLimiter.RecordFailure(context);
            return AuthWorkflowResult.BadRequest("\u91cd\u7f6e\u94fe\u63a5\u65e0\u6548\u6216\u5df2\u8fc7\u671f");
        }

        user.PasswordHash = BCrypt.Net.BCrypt.HashPassword(request.NewPassword);
        user.PasswordResetToken = null;
        user.PasswordResetExpiresAt = null;

        await _sessions.ClearUserSessionsAsync(db, user.Id);
        await db.SaveChangesAsync();

        _rateLimiter.Reset(context);
        return AuthWorkflowResult.Ok("\u5bc6\u7801\u91cd\u7f6e\u6210\u529f\uff0c\u8bf7\u4f7f\u7528\u65b0\u5bc6\u7801\u767b\u5f55");
    }

    public async Task<AuthWorkflowResult> VerifyEmailAsync(VerifyEmailRequest request, HttpContext context)
    {
        if (_rateLimiter.IsLimited(context))
        {
            return AuthWorkflowResult.RateLimited();
        }

        if (string.IsNullOrWhiteSpace(request.Token))
        {
            return AuthWorkflowResult.BadRequest("\u9a8c\u8bc1 Token \u4e0d\u80fd\u4e3a\u7a7a");
        }

        await using var db = await _dbFactory.CreateDbContextAsync();
        var user = await db.Users.FirstOrDefaultAsync(u => u.VerificationToken == request.Token);

        if (user == null)
        {
            _rateLimiter.RecordFailure(context);
            return AuthWorkflowResult.BadRequest("\u9a8c\u8bc1\u94fe\u63a5\u65e0\u6548");
        }

        user.EmailVerified = true;
        user.VerificationToken = null;
        await db.SaveChangesAsync();

        _rateLimiter.Reset(context);
        return AuthWorkflowResult.Ok("\u90ae\u7bb1\u9a8c\u8bc1\u6210\u529f");
    }

    public async Task<AuthWorkflowResult> ResendVerificationAsync(HttpContext context)
    {
        if (_rateLimiter.IsLimited(context))
        {
            return AuthWorkflowResult.RateLimited();
        }

        var sessionToken = _cookies.ReadSessionToken(context);
        if (string.IsNullOrEmpty(sessionToken))
        {
            return AuthWorkflowResult.Unauthorized();
        }

        await using var db = await _dbFactory.CreateDbContextAsync();
        var session = await db.Sessions
            .Include(s => s.User)
            .FirstOrDefaultAsync(s => s.Token == sessionToken && s.ExpiresAt > DateTime.UtcNow);

        if (session?.User == null)
        {
            return AuthWorkflowResult.Unauthorized();
        }

        if (session.User.EmailVerified)
        {
            return AuthWorkflowResult.Ok("\u90ae\u7bb1\u5df2\u9a8c\u8bc1\uff0c\u65e0\u9700\u91cd\u590d\u64cd\u4f5c");
        }

        session.User.VerificationToken = _tokens.Generate();
        await db.SaveChangesAsync();

        SendVerificationEmailInBackground(session.User.Email, session.User.VerificationToken);
        _rateLimiter.RecordFailure(context);

        return AuthWorkflowResult.Ok("\u9a8c\u8bc1\u90ae\u4ef6\u5df2\u53d1\u9001\uff0c\u8bf7\u67e5\u6536");
    }

    private void SendVerificationEmailInBackground(string email, string? token)
    {
        if (string.IsNullOrEmpty(token))
        {
            return;
        }

        _ = Task.Run(async () =>
        {
            try
            {
                await _email.SendVerificationEmailAsync(email, token);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Verification email failed, Email={Email}", email);
            }
        });
    }

    private void SendPasswordResetEmailInBackground(string email, string? token)
    {
        if (string.IsNullOrEmpty(token))
        {
            return;
        }

        _ = Task.Run(async () =>
        {
            try
            {
                await _email.SendPasswordResetEmailAsync(email, token);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Password reset email failed, Email={Email}", email);
            }
        });
    }

    private static UserInfo ToUserInfo(User user) =>
        new(user.Id, user.Email, user.EmailVerified);
}
