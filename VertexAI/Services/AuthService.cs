using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using VertexAI.Data;
using VertexAI.Data.Entities;
using VertexAI.Api;

namespace VertexAI.Services;

/// <summary>
/// 本地认证服务 - 基于 PostgreSQL + BCrypt 密码哈希 + HttpOnly Cookie 持久化
/// 注意：此服务现在只维护当前用户状态，实际的登录/登出由 API 端点完成
/// </summary>
public class AuthService
{
    private readonly IDbContextFactory<AppDbContext> _dbFactory;
    private readonly IHttpContextAccessor _httpContextAccessor;
    private readonly ILogger<AuthService> _logger;
    private CurrentUser _currentUser = new();
    private const string SessionCookieName = "gemini_auth";

    public CurrentUser CurrentUser => _currentUser;
    public bool IsAuthenticated => _currentUser.IsAuthenticated;

    public event Action? OnAuthStateChanged;

    public AuthService(
        IDbContextFactory<AppDbContext> dbFactory,
        IHttpContextAccessor httpContextAccessor,
        ILogger<AuthService> logger)
    {
        _dbFactory = dbFactory;
        _httpContextAccessor = httpContextAccessor;
        _logger = logger;
    }

    /// <summary>
    /// 初始化：从 HttpOnly Cookie 恢复会话
    /// </summary>
    public async Task InitializeAsync()
    {
        try
        {
            var httpContext = _httpContextAccessor.HttpContext;
            if (httpContext == null)
            {
                _logger.LogDebug("HttpContext 不可用 (SignalR 连接后期)");
                return;
            }

            var token = httpContext.Request.Cookies[SessionCookieName];
            if (string.IsNullOrEmpty(token))
            {
                _logger.LogDebug("未找到认证 Cookie");
                return;
            }

            await using var db = await _dbFactory.CreateDbContextAsync();
            var session = await db.Sessions
                .Include(s => s.User)
                .FirstOrDefaultAsync(s => s.Token == token && s.ExpiresAt > DateTime.UtcNow);

            if (session?.User != null)
            {
                SetCurrentUser(session.User);
                _logger.LogInformation("会话恢复成功, UserId={UserId}, Email={Email}",
                    session.User.Id, session.User.Email);
            }
            else
            {
                _logger.LogDebug("Cookie 中的 Token 无效或已过期");
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "恢复会话失败");
        }
    }

    /// <summary>
    /// 直接设置当前用户（供 Blazor 组件在收到 API 响应后调用）
    /// </summary>
    public void SetAuthenticatedUser(UserInfo userInfo)
    {
        _currentUser = new CurrentUser
        {
            Id = userInfo.Id,
            Email = userInfo.Email,
            IsAuthenticated = true
        };
        OnAuthStateChanged?.Invoke();
    }

    /// <summary>
    /// 清除当前用户状态
    /// </summary>
    public void ClearAuthentication()
    {
        _currentUser = new CurrentUser();
        OnAuthStateChanged?.Invoke();
    }

    /// <summary>
    /// 登出（供 Blazor 组件调用，实际 Cookie 清除在登出后页面刷新时由服务器处理）
    /// </summary>
    public Task SignOutAsync()
    {
        ClearAuthentication();
        return Task.CompletedTask;
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
