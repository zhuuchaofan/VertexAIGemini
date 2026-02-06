using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using VertexAI.Data;
using VertexAI.Data.Entities;

namespace VertexAI.Api;

/// <summary>
/// 认证 API 端点 (Minimal API)
/// 使用 HttpOnly Cookie 存储 Session Token
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

    public static void MapAuthEndpoints(this WebApplication app)
    {
        var group = app.MapGroup("/api/auth");

        group.MapPost("/login", LoginAsync);
        group.MapPost("/register", RegisterAsync);
        group.MapPost("/logout", Logout);
        group.MapGet("/status", GetStatusAsync);
    }

    /// <summary>
    /// 用户登录
    /// </summary>
    private static async Task<IResult> LoginAsync(
        AuthRequest request,
        IDbContextFactory<AppDbContext> dbFactory,
        HttpContext httpContext)
    {
        if (string.IsNullOrWhiteSpace(request.Email) || string.IsNullOrWhiteSpace(request.Password))
        {
            return Results.BadRequest(new AuthResponse(false, "邮箱和密码不能为空"));
        }

        await using var db = await dbFactory.CreateDbContextAsync();

        var user = await db.Users.FirstOrDefaultAsync(u => u.Email == request.Email.ToLower());
        if (user == null || !BCrypt.Net.BCrypt.Verify(request.Password, user.PasswordHash))
        {
            return Results.BadRequest(new AuthResponse(false, "邮箱或密码错误"));
        }

        // 更新最后登录时间
        user.LastLoginAt = DateTime.UtcNow;

        // 创建新 Session
        var token = Guid.NewGuid().ToString("N");
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

        return Results.Ok(new AuthResponse(true, null, new UserInfo(user.Id, user.Email)));
    }

    /// <summary>
    /// 用户注册
    /// </summary>
    private static async Task<IResult> RegisterAsync(
        AuthRequest request,
        IDbContextFactory<AppDbContext> dbFactory,
        HttpContext httpContext)
    {
        if (string.IsNullOrWhiteSpace(request.Email) || string.IsNullOrWhiteSpace(request.Password))
        {
            return Results.BadRequest(new AuthResponse(false, "邮箱和密码不能为空"));
        }

        if (request.Password.Length < 6)
        {
            return Results.BadRequest(new AuthResponse(false, "密码长度至少 6 个字符"));
        }

        await using var db = await dbFactory.CreateDbContextAsync();

        if (await db.Users.AnyAsync(u => u.Email == request.Email.ToLower()))
        {
            return Results.BadRequest(new AuthResponse(false, "该邮箱已被注册"));
        }

        var user = new User
        {
            Email = request.Email.ToLower(),
            PasswordHash = BCrypt.Net.BCrypt.HashPassword(request.Password)
        };

        db.Users.Add(user);
        await db.SaveChangesAsync();

        // 注册后自动登录
        var token = Guid.NewGuid().ToString("N");
        var session = new Session
        {
            UserId = user.Id,
            Token = token,
            ExpiresAt = DateTime.UtcNow.AddDays(7)
        };

        db.Sessions.Add(session);
        await db.SaveChangesAsync();

        httpContext.Response.Cookies.Append(SessionCookieName, token, CookieOptions);

        return Results.Ok(new AuthResponse(true, null, new UserInfo(user.Id, user.Email)));
    }

    /// <summary>
    /// 用户登出
    /// </summary>
    private static IResult Logout(HttpContext httpContext)
    {
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

        return Results.Ok(new AuthResponse(true, null, new UserInfo(session.User.Id, session.User.Email)));
    }
}

// DTOs
public record AuthRequest(string Email, string Password);
public record AuthResponse(bool Success, string? Error, UserInfo? User = null);
public record UserInfo(Guid Id, string Email);
