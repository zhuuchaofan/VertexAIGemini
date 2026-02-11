using System.Collections.Concurrent;
using System.Security.Cryptography;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using VertexAI.Data;
using VertexAI.Data.Entities;
using VertexAI.Services;

namespace VertexAI.Api;

/// <summary>
/// 认证 API 端点 (Minimal API)
/// 使用 HttpOnly Cookie + 加密随机 Session Token + IP 速率限制
/// </summary>
public static class AuthEndpoints
{
    private const string SessionCookieName = "gemini_auth";
    private static readonly CookieOptions CookieOptions = new()
    {
        HttpOnly = true,
        Secure = true,
        SameSite = SameSiteMode.Strict,
        MaxAge = TimeSpan.FromDays(7)
    };

    // ──────────────────────────────────────────────
    // 速率限制：基于 IP 的内存计数器
    // ──────────────────────────────────────────────
    private static readonly ConcurrentDictionary<string, RateLimitEntry> _rateLimits = new();
    private const int MaxAttemptsPerMinute = 5;

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

    /// <summary>
    /// 用户登录 - 含速率限制和过期 Session 清理
    /// </summary>
    private static async Task<IResult> LoginAsync(
        AuthRequest request,
        IDbContextFactory<AppDbContext> dbFactory,
        HttpContext httpContext)
    {
        var clientIp = GetClientIp(httpContext);

        // 速率限制检查
        if (IsRateLimited(clientIp))
        {
            return Results.Json(
                new AuthResponse(false, "操作过于频繁，请稍后再试"),
                statusCode: 429);
        }

        if (string.IsNullOrWhiteSpace(request.Email) || string.IsNullOrWhiteSpace(request.Password))
        {
            return Results.BadRequest(new AuthResponse(false, "邮箱和密码不能为空"));
        }

        await using var db = await dbFactory.CreateDbContextAsync();

        var email = NormalizeEmail(request.Email);
        var user = await db.Users.FirstOrDefaultAsync(u => u.Email == email);
        if (user == null || !BCrypt.Net.BCrypt.Verify(request.Password, user.PasswordHash))
        {
            RecordFailedAttempt(clientIp);
            return Results.BadRequest(new AuthResponse(false, "邮箱或密码错误"));
        }

        // 更新最后登录时间
        user.LastLoginAt = DateTime.UtcNow;

        // 清理该用户的过期 Session（防止表膨胀）
        var expiredSessions = await db.Sessions
            .Where(s => s.UserId == user.Id && s.ExpiresAt <= DateTime.UtcNow)
            .ToListAsync();
        if (expiredSessions.Count > 0)
        {
            db.Sessions.RemoveRange(expiredSessions);
        }

        // 创建新 Session（加密随机 Token）
        var token = GenerateSecureToken();
        var session = new Session
        {
            UserId = user.Id,
            Token = token,
            ExpiresAt = DateTime.UtcNow.AddDays(7)
        };

        db.Sessions.Add(session);
        await db.SaveChangesAsync();

        // 设置 HttpOnly Cookie
        httpContext.Response.Cookies.Append(SessionCookieName, token, CookieOptions);

        ResetRateLimit(clientIp);
        return Results.Ok(new AuthResponse(true, null, new UserInfo(user.Id, user.Email, user.EmailVerified)));
    }

    /// <summary>
    /// 用户注册 - 含密码强度校验
    /// </summary>
    private static async Task<IResult> RegisterAsync(
        AuthRequest request,
        IDbContextFactory<AppDbContext> dbFactory,
        HttpContext httpContext,
        EmailService emailService)
    {
        var clientIp = GetClientIp(httpContext);

        // 速率限制检查（注册也限制，防止批量注册）
        if (IsRateLimited(clientIp))
        {
            return Results.Json(
                new AuthResponse(false, "操作过于频繁，请稍后再试"),
                statusCode: 429);
        }

        if (string.IsNullOrWhiteSpace(request.Email) || string.IsNullOrWhiteSpace(request.Password))
        {
            return Results.BadRequest(new AuthResponse(false, "邮箱和密码不能为空"));
        }

        // 密码强度校验
        var passwordError = ValidatePasswordStrength(request.Password);
        if (passwordError != null)
        {
            return Results.BadRequest(new AuthResponse(false, passwordError));
        }

        var email = NormalizeEmail(request.Email);

        // 邮箱格式校验（正则）
        if (!System.Text.RegularExpressions.Regex.IsMatch(email, @"^[^@\s]+@[^@\s]+\.[^@\s]+$"))
        {
            return Results.BadRequest(new AuthResponse(false, "请输入有效的邮箱地址"));
        }

        await using var db = await dbFactory.CreateDbContextAsync();

        if (await db.Users.AnyAsync(u => u.Email == email))
        {
            RecordFailedAttempt(clientIp);
            return Results.BadRequest(new AuthResponse(false, "该邮箱已被注册"));
        }

        var user = new User
        {
            Email = email,
            PasswordHash = BCrypt.Net.BCrypt.HashPassword(request.Password),
            EmailVerified = false,
            VerificationToken = GenerateSecureToken()
        };

        db.Users.Add(user);
        await db.SaveChangesAsync();

        // 注册后自动登录（加密随机 Token）
        var token = GenerateSecureToken();
        var session = new Session
        {
            UserId = user.Id,
            Token = token,
            ExpiresAt = DateTime.UtcNow.AddDays(7)
        };

        db.Sessions.Add(session);
        await db.SaveChangesAsync();

        httpContext.Response.Cookies.Append(SessionCookieName, token, CookieOptions);

        ResetRateLimit(clientIp);

        // 异步发送验证邮件（不阻塞注册流程）
        _ = Task.Run(async () =>
        {
            try { await emailService.SendVerificationEmailAsync(user.Email, user.VerificationToken!); }
            catch { /* 日志已在 EmailService 内部记录 */ }
        });

        return Results.Ok(new AuthResponse(true, null, new UserInfo(user.Id, user.Email, user.EmailVerified)));
    }

    /// <summary>
    /// 用户登出 - 同时清除服务端 Session 和客户端 Cookie
    /// </summary>
    private static async Task<IResult> LogoutAsync(
        IDbContextFactory<AppDbContext> dbFactory,
        HttpContext httpContext)
    {
        var token = httpContext.Request.Cookies[SessionCookieName];

        // 清除服务端 Session
        if (!string.IsNullOrEmpty(token))
        {
            await using var db = await dbFactory.CreateDbContextAsync();
            var session = await db.Sessions.FirstOrDefaultAsync(s => s.Token == token);
            if (session != null)
            {
                db.Sessions.Remove(session);
                await db.SaveChangesAsync();
            }
        }

        // 清除客户端 Cookie
        httpContext.Response.Cookies.Delete(SessionCookieName);
        return Results.Ok(new AuthResponse(true, "已登出"));
    }

    /// <summary>
    /// 获取当前登录状态
    /// </summary>
    private static async Task<IResult> GetStatusAsync(
        IDbContextFactory<AppDbContext> dbFactory,
        HttpContext httpContext)
    {
        var token = httpContext.Request.Cookies[SessionCookieName];
        if (string.IsNullOrEmpty(token))
        {
            return Results.Ok(new AuthResponse(false, null));
        }

        await using var db = await dbFactory.CreateDbContextAsync();
        var session = await db.Sessions
            .Include(s => s.User)
            .FirstOrDefaultAsync(s => s.Token == token && s.ExpiresAt > DateTime.UtcNow);

        if (session?.User == null)
        {
            httpContext.Response.Cookies.Delete(SessionCookieName);
            return Results.Ok(new AuthResponse(false, null));
        }

        return Results.Ok(new AuthResponse(true, null, new UserInfo(session.User.Id, session.User.Email, session.User.EmailVerified)));
    }

    // ──────────────────────────────────────────────
    // 密码重置 & 邮箱验证
    // ──────────────────────────────────────────────

    /// <summary>
    /// 忘记密码 - 生成重置 Token（有效期 1 小时）
    /// </summary>
    private static async Task<IResult> ForgotPasswordAsync(
        ForgotPasswordRequest request,
        IDbContextFactory<AppDbContext> dbFactory,
        HttpContext httpContext,
        EmailService emailService,
        ILoggerFactory loggerFactory)
    {
        var logger = loggerFactory.CreateLogger("AuthEndpoints");
        var clientIp = GetClientIp(httpContext);
        if (IsRateLimited(clientIp))
        {
            return Results.Json(
                new AuthResponse(false, "操作过于频繁，请稍后再试"),
                statusCode: 429);
        }

        if (string.IsNullOrWhiteSpace(request.Email))
        {
            return Results.BadRequest(new AuthResponse(false, "请输入邮箱地址"));
        }

        var email = NormalizeEmail(request.Email);
        await using var db = await dbFactory.CreateDbContextAsync();
        var user = await db.Users.FirstOrDefaultAsync(u => u.Email == email);

        // 无论用户是否存在，都返回相同消息（防止邮箱枚举攻击）
        if (user != null)
        {
            user.PasswordResetToken = GenerateSecureToken();
            user.PasswordResetExpiresAt = DateTime.UtcNow.AddHours(1);
            await db.SaveChangesAsync();

            logger.LogInformation("密码重置 Token 已生成, Email={Email}", email);

            // 异步发送重置邮件
            _ = Task.Run(async () =>
            {
                try { await emailService.SendPasswordResetEmailAsync(user.Email, user.PasswordResetToken!); }
                catch { /* 日志已在 EmailService 内部记录 */ }
            });
        }

        RecordFailedAttempt(clientIp);
        return Results.Ok(new AuthResponse(true, "如果该邮箱已注册，重置链接将发送到您的邮箱"));
    }

    /// <summary>
    /// 重置密码 - 验证 Token 后更新密码
    /// </summary>
    private static async Task<IResult> ResetPasswordAsync(
        ResetPasswordRequest request,
        IDbContextFactory<AppDbContext> dbFactory,
        HttpContext httpContext)
    {
        var clientIp = GetClientIp(httpContext);
        if (IsRateLimited(clientIp))
        {
            return Results.Json(
                new AuthResponse(false, "操作过于频繁，请稍后再试"),
                statusCode: 429);
        }

        if (string.IsNullOrWhiteSpace(request.Token) || string.IsNullOrWhiteSpace(request.NewPassword))
        {
            return Results.BadRequest(new AuthResponse(false, "Token 和新密码不能为空"));
        }

        var passwordError = ValidatePasswordStrength(request.NewPassword);
        if (passwordError != null)
        {
            return Results.BadRequest(new AuthResponse(false, passwordError));
        }

        await using var db = await dbFactory.CreateDbContextAsync();
        var user = await db.Users.FirstOrDefaultAsync(u =>
            u.PasswordResetToken == request.Token &&
            u.PasswordResetExpiresAt > DateTime.UtcNow);

        if (user == null)
        {
            RecordFailedAttempt(clientIp);
            return Results.BadRequest(new AuthResponse(false, "重置链接无效或已过期"));
        }

        user.PasswordHash = BCrypt.Net.BCrypt.HashPassword(request.NewPassword);
        user.PasswordResetToken = null;
        user.PasswordResetExpiresAt = null;

        // 使该用户所有现有 Session 失效（安全起见）
        var sessions = await db.Sessions.Where(s => s.UserId == user.Id).ToListAsync();
        db.Sessions.RemoveRange(sessions);

        await db.SaveChangesAsync();

        ResetRateLimit(clientIp);
        return Results.Ok(new AuthResponse(true, "密码重置成功，请使用新密码登录"));
    }

    /// <summary>
    /// 邮箱验证 - 验证 Token 后标记邮箱已验证
    /// </summary>
    private static async Task<IResult> VerifyEmailAsync(
        VerifyEmailRequest request,
        IDbContextFactory<AppDbContext> dbFactory,
        HttpContext httpContext)
    {
        var clientIp = GetClientIp(httpContext);
        if (IsRateLimited(clientIp))
        {
            return Results.Json(
                new AuthResponse(false, "操作过于频繁，请稍后再试"),
                statusCode: 429);
        }

        if (string.IsNullOrWhiteSpace(request.Token))
        {
            return Results.BadRequest(new AuthResponse(false, "验证 Token 不能为空"));
        }

        await using var db = await dbFactory.CreateDbContextAsync();
        var user = await db.Users.FirstOrDefaultAsync(u => u.VerificationToken == request.Token);

        if (user == null)
        {
            RecordFailedAttempt(clientIp);
            return Results.BadRequest(new AuthResponse(false, "验证链接无效"));
        }

        user.EmailVerified = true;
        user.VerificationToken = null;
        await db.SaveChangesAsync();

        ResetRateLimit(clientIp);
        return Results.Ok(new AuthResponse(true, "邮箱验证成功"));
    }

    /// <summary>
    /// 重发验证邮件 - 为当前登录用户重新生成并发送验证邮件
    /// </summary>
    private static async Task<IResult> ResendVerificationAsync(
        IDbContextFactory<AppDbContext> dbFactory,
        HttpContext httpContext,
        EmailService emailService)
    {
        var clientIp = GetClientIp(httpContext);
        if (IsRateLimited(clientIp))
        {
            return Results.Json(
                new AuthResponse(false, "操作过于频繁，请稍后再试"),
                statusCode: 429);
        }

        // 通过 Cookie 获取当前用户
        var sessionToken = httpContext.Request.Cookies[SessionCookieName];
        if (string.IsNullOrEmpty(sessionToken))
        {
            return Results.Unauthorized();
        }

        await using var db = await dbFactory.CreateDbContextAsync();
        var session = await db.Sessions
            .Include(s => s.User)
            .FirstOrDefaultAsync(s => s.Token == sessionToken && s.ExpiresAt > DateTime.UtcNow);

        if (session?.User == null)
        {
            return Results.Unauthorized();
        }

        if (session.User.EmailVerified)
        {
            return Results.Ok(new AuthResponse(true, "邮箱已验证，无需重复操作"));
        }

        // 生成新 Token
        session.User.VerificationToken = GenerateSecureToken();
        await db.SaveChangesAsync();

        // 异步发送验证邮件
        var email = session.User.Email;
        var token = session.User.VerificationToken;
        _ = Task.Run(async () =>
        {
            try { await emailService.SendVerificationEmailAsync(email, token); }
            catch { /* 日志已在 EmailService 内部记录 */ }
        });

        RecordFailedAttempt(clientIp);
        return Results.Ok(new AuthResponse(true, "验证邮件已发送，请查收"));
    }

    // ──────────────────────────────────────────────
    // 私有辅助方法
    // ──────────────────────────────────────────────

    /// <summary>
    /// 生成 32 字节加密随机 Token（Base64URL 编码）
    /// </summary>
    private static string GenerateSecureToken()
    {
        var bytes = RandomNumberGenerator.GetBytes(32);
        return Convert.ToBase64String(bytes)
            .Replace("+", "-")
            .Replace("/", "_")
            .TrimEnd('=');
    }

    /// <summary>
    /// 邮箱规范化
    /// </summary>
    private static string NormalizeEmail(string email)
        => email.Trim().ToLowerInvariant();

    /// <summary>
    /// 密码强度校验：至少 6 字符，且包含字母和数字
    /// </summary>
    private static string? ValidatePasswordStrength(string password)
    {
        if (password.Length < 6)
            return "密码长度至少需要 6 个字符";
        if (password.Length > 100)
            return "密码长度不能超过 100 个字符";

        var hasLetter = false;
        var hasDigit = false;
        foreach (var c in password)
        {
            if (char.IsLetter(c)) hasLetter = true;
            if (char.IsDigit(c)) hasDigit = true;
            if (hasLetter && hasDigit) break;
        }

        if (!hasLetter || !hasDigit)
            return "密码需要同时包含字母和数字";

        return null;
    }

    /// <summary>
    /// 获取客户端 IP（支持代理转发）
    /// </summary>
    private static string GetClientIp(HttpContext context)
    {
        var forwarded = context.Request.Headers["X-Forwarded-For"].FirstOrDefault();
        if (!string.IsNullOrEmpty(forwarded))
        {
            return forwarded.Split(',')[0].Trim();
        }
        return context.Connection.RemoteIpAddress?.ToString() ?? "unknown";
    }

    /// <summary>
    /// 检查是否被速率限制
    /// </summary>
    private static bool IsRateLimited(string clientIp)
    {
        if (!_rateLimits.TryGetValue(clientIp, out var entry))
            return false;

        // 超过窗口期则重置
        if (DateTime.UtcNow - entry.WindowStart > TimeSpan.FromMinutes(1))
        {
            _rateLimits.TryRemove(clientIp, out _);
            return false;
        }

        return entry.Attempts >= MaxAttemptsPerMinute;
    }

    /// <summary>
    /// 记录失败尝试
    /// </summary>
    private static void RecordFailedAttempt(string clientIp)
    {
        _rateLimits.AddOrUpdate(clientIp,
            _ => new RateLimitEntry { Attempts = 1, WindowStart = DateTime.UtcNow },
            (_, existing) =>
            {
                if (DateTime.UtcNow - existing.WindowStart > TimeSpan.FromMinutes(1))
                {
                    return new RateLimitEntry { Attempts = 1, WindowStart = DateTime.UtcNow };
                }
                existing.Attempts++;
                return existing;
            });
    }

    /// <summary>
    /// 登录成功后重置速率计数
    /// </summary>
    private static void ResetRateLimit(string clientIp)
    {
        _rateLimits.TryRemove(clientIp, out _);
    }
}

/// <summary>
/// 速率限制条目
/// </summary>
internal class RateLimitEntry
{
    public int Attempts { get; set; }
    public DateTime WindowStart { get; set; }
}

// DTOs
public record AuthRequest(string Email, string Password);
public record AuthResponse(bool Success, string? Error, UserInfo? User = null);
public record UserInfo(Guid Id, string Email, bool EmailVerified = false);
public record ForgotPasswordRequest(string Email);
public record ResetPasswordRequest(string Token, string NewPassword);
public record VerifyEmailRequest(string Token);
