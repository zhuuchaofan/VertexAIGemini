using Microsoft.Extensions.Options;
using Supabase;
using Supabase.Gotrue;
using Client = Supabase.Client;

namespace VertexAI.Services;

/// <summary>
/// 认证服务 - 封装 Supabase Auth 操作
/// </summary>
public class AuthService : IAsyncDisposable
{
    private readonly Client _supabase;
    private CurrentUser _currentUser = new();

    public CurrentUser CurrentUser => _currentUser;
    public bool IsAuthenticated => _currentUser.IsAuthenticated;

    public event Action? OnAuthStateChanged;

    public AuthService(IOptions<SupabaseSettings> settings)
    {
        var config = settings.Value;
        _supabase = new Client(config.Url, config.Key);
    }

    /// <summary>
    /// 初始化 Supabase 客户端
    /// </summary>
    public async Task InitializeAsync()
    {
        await _supabase.InitializeAsync();
    }

    /// <summary>
    /// 用户注册
    /// </summary>
    public async Task<(bool Success, string? Error)> SignUpAsync(string email, string password)
    {
        try
        {
            var response = await _supabase.Auth.SignUp(email, password);
            if (response?.User != null)
            {
                UpdateCurrentUser(response.User);
                return (true, null);
            }
            return (false, "注册失败，请稍后重试");
        }
        catch (Exception ex)
        {
            return (false, ex.Message);
        }
    }

    /// <summary>
    /// 用户登录
    /// </summary>
    public async Task<(bool Success, string? Error)> SignInAsync(string email, string password)
    {
        try
        {
            var response = await _supabase.Auth.SignIn(email, password);
            if (response?.User != null)
            {
                UpdateCurrentUser(response.User);
                return (true, null);
            }
            return (false, "登录失败，邮箱或密码错误");
        }
        catch (Exception ex)
        {
            return (false, ex.Message);
        }
    }

    /// <summary>
    /// 用户登出
    /// </summary>
    public async Task SignOutAsync()
    {
        try
        {
            await _supabase.Auth.SignOut();
        }
        catch
        {
            // 忽略登出错误
        }
        finally
        {
            _currentUser = new CurrentUser();
            OnAuthStateChanged?.Invoke();
        }
    }

    /// <summary>
    /// 检查当前会话
    /// </summary>
    public async Task<bool> CheckSessionAsync()
    {
        try
        {
            var session = await _supabase.Auth.RetrieveSessionAsync();
            if (session?.User != null)
            {
                UpdateCurrentUser(session.User);
                return true;
            }
        }
        catch
        {
            // 会话无效
        }

        _currentUser = new CurrentUser();
        return false;
    }

    private void UpdateCurrentUser(User user)
    {
        _currentUser = new CurrentUser
        {
            Id = Guid.Parse(user.Id!),
            Email = user.Email ?? "",
            IsAuthenticated = true
        };
        OnAuthStateChanged?.Invoke();
    }

    public ValueTask DisposeAsync()
    {
        return ValueTask.CompletedTask;
    }
}
