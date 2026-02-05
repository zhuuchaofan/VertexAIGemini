using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.JSInterop;
using VertexAI.Data;
using VertexAI.Data.Entities;

namespace VertexAI.Services;

/// <summary>
/// 本地认证服务 - 基于 PostgreSQL + BCrypt 密码哈希 + 简单 Token 持久化
/// </summary>
public class AuthService
{
    private readonly IDbContextFactory<AppDbContext> _dbFactory;
    private readonly IJSRuntime _js;
    private readonly ILogger<AuthService> _logger;
    private CurrentUser _currentUser = new();
    private const string SessionKey = "gemini_chat_session";

    public CurrentUser CurrentUser => _currentUser;
    public bool IsAuthenticated => _currentUser.IsAuthenticated;

    public event Action? OnAuthStateChanged;

    public AuthService(IDbContextFactory<AppDbContext> dbFactory, IJSRuntime js, ILogger<AuthService> logger)
    {
        _dbFactory = dbFactory;
        _js = js;
        _logger = logger;
    }

    /// <summary>
    /// 初始化：从 localStorage 恢复会话
    /// </summary>
    public async Task InitializeAsync()
    {
        try
        {
            var token = await _js.InvokeAsync<string?>("localStorage.getItem", SessionKey);
            if (string.IsNullOrEmpty(token)) return;

            await using var db = await _dbFactory.CreateDbContextAsync();
            var session = await db.Sessions
                .Include(s => s.User)
                .FirstOrDefaultAsync(s => s.Token == token && s.ExpiresAt > DateTime.UtcNow);

            if (session != null && session.User != null)
            {
                SetCurrentUser(session.User);
                _logger.LogInformation("会话恢复成功, UserId={UserId}, Email={Email}",
                    session.User.Id, session.User.Email);
            }
            else
            {
                // 会话无效，清除
                await _js.InvokeVoidAsync("localStorage.removeItem", SessionKey);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "恢复会话失败");
        }
    }

    /// <summary>
    /// 用户注册
    /// </summary>
    public async Task<(bool Success, string? Error)> SignUpAsync(string email, string password)
    {
        if (string.IsNullOrWhiteSpace(email) || string.IsNullOrWhiteSpace(password))
        {
            return (false, "邮箱和密码不能为空");
        }

        if (password.Length < 6)
        {
            return (false, "密码长度至少 6 个字符");
        }

        try
        {
            await using var db = await _dbFactory.CreateDbContextAsync();

            if (await db.Users.AnyAsync(u => u.Email == email.ToLower()))
            {
                return (false, "该邮箱已被注册");
            }

            var user = new User
            {
                Email = email.ToLower(),
                PasswordHash = BCrypt.Net.BCrypt.HashPassword(password)
            };

            db.Users.Add(user);
            await db.SaveChangesAsync();

            _logger.LogInformation("用户注册成功, UserId={UserId}, Email={Email}",
                user.Id, user.Email);

            await CreateSessionAsync(db, user);
            return (true, null);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "注册失败, Email={Email}", email);
            return (false, "注册失败，请稍后重试");
        }
    }

    /// <summary>
    /// 用户登录
    /// </summary>
    public async Task<(bool Success, string? Error)> SignInAsync(string email, string password)
    {
        if (string.IsNullOrWhiteSpace(email) || string.IsNullOrWhiteSpace(password))
        {
            return (false, "邮箱和密码不能为空");
        }

        try
        {
            await using var db = await _dbFactory.CreateDbContextAsync();

            var user = await db.Users.FirstOrDefaultAsync(u => u.Email == email.ToLower());
            if (user == null || !BCrypt.Net.BCrypt.Verify(password, user.PasswordHash))
            {
                return (false, "邮箱或密码错误");
            }

            user.LastLoginAt = DateTime.UtcNow;
            await CreateSessionAsync(db, user);
            await db.SaveChangesAsync();

            _logger.LogInformation("用户登录成功, UserId={UserId}, Email={Email}",
                user.Id, user.Email);

            return (true, null);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "登录失败, Email={Email}", email);
            return (false, "登录失败，请稍后重试");
        }
    }

    /// <summary>
    /// 创建新会话并保存到 localStorage
    /// </summary>
    private async Task CreateSessionAsync(AppDbContext db, User user)
    {
        var token = Guid.NewGuid().ToString("N");
        var session = new Session
        {
            UserId = user.Id,
            Token = token,
            ExpiresAt = DateTime.UtcNow.AddDays(7) // 7天过期
        };

        db.Sessions.Add(session);
        await db.SaveChangesAsync();

        await _js.InvokeVoidAsync("localStorage.setItem", SessionKey, token);
        SetCurrentUser(user);
    }

    /// <summary>
    /// 用户登出
    /// </summary>
    public async Task SignOutAsync()
    {
        try
        {
            await _js.InvokeVoidAsync("localStorage.removeItem", SessionKey);
            // 可选：同时删除数据库中的会话（如果需要更严格的安全控制）
        }
        catch { }
        finally
        {
            _currentUser = new CurrentUser();
            OnAuthStateChanged?.Invoke();
        }
    }

    private void SetCurrentUser(User user)
    {
        _currentUser = new CurrentUser
        {
            Id = user.Id,
            Email = user.Email,
            IsAuthenticated = true
        };
        OnAuthStateChanged?.Invoke();
    }
}

/// <summary>
/// 当前用户信息
/// </summary>
public class CurrentUser
{
    public Guid Id { get; set; }
    public string Email { get; set; } = "";
    public bool IsAuthenticated { get; set; }
}
