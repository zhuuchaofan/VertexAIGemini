using Microsoft.EntityFrameworkCore;
using VertexAI.Data;
using VertexAI.Data.Entities;

namespace VertexAI.Services;

/// <summary>
/// 本地认证服务 - 基于 PostgreSQL + BCrypt 密码哈希
/// </summary>
public class AuthService
{
    private readonly IDbContextFactory<AppDbContext> _dbFactory;
    private CurrentUser _currentUser = new();

    public CurrentUser CurrentUser => _currentUser;
    public bool IsAuthenticated => _currentUser.IsAuthenticated;

    public event Action? OnAuthStateChanged;

    public AuthService(IDbContextFactory<AppDbContext> dbFactory)
    {
        _dbFactory = dbFactory;
    }

    /// <summary>
    /// 初始化（本地认证不需要特殊初始化）
    /// </summary>
    public Task InitializeAsync() => Task.CompletedTask;

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

            // 检查邮箱是否已存在
            var exists = await db.Users.AnyAsync(u => u.Email == email.ToLower());
            if (exists)
            {
                return (false, "该邮箱已被注册");
            }

            // 创建用户
            var user = new User
            {
                Email = email.ToLower(),
                PasswordHash = BCrypt.Net.BCrypt.HashPassword(password)
            };

            db.Users.Add(user);
            await db.SaveChangesAsync();

            // 自动登录
            SetCurrentUser(user);
            return (true, null);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"注册失败: {ex.Message}");
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
            if (user == null)
            {
                return (false, "邮箱或密码错误");
            }

            // 验证密码
            if (!BCrypt.Net.BCrypt.Verify(password, user.PasswordHash))
            {
                return (false, "邮箱或密码错误");
            }

            // 更新最后登录时间
            user.LastLoginAt = DateTime.UtcNow;
            await db.SaveChangesAsync();

            SetCurrentUser(user);
            return (true, null);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"登录失败: {ex.Message}");
            return (false, "登录失败，请稍后重试");
        }
    }

    /// <summary>
    /// 用户登出
    /// </summary>
    public Task SignOutAsync()
    {
        _currentUser = new CurrentUser();
        OnAuthStateChanged?.Invoke();
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
