using Microsoft.Extensions.Options;
using Microsoft.JSInterop;
using Supabase;
using Supabase.Gotrue;
using Client = Supabase.Client;

namespace VertexAI.Services;

/// <summary>
/// 认证服务 - 封装 Supabase Auth 操作，支持会话持久化
/// </summary>
public class AuthService : IAsyncDisposable
{
    private readonly Client _supabase;
    private readonly IJSRuntime _js;
    private CurrentUser _currentUser = new();
    private bool _initialized;
    private const string SessionKey = "supabase_session";

    public CurrentUser CurrentUser => _currentUser;
    public bool IsAuthenticated => _currentUser.IsAuthenticated;

    public event Action? OnAuthStateChanged;

    public AuthService(IOptions<SupabaseSettings> settings, IJSRuntime js)
    {
        var config = settings.Value;
        _supabase = new Client(config.Url, config.Key);
        _js = js;
    }

    /// <summary>
    /// 初始化 Supabase 客户端并恢复会话
    /// </summary>
    public async Task InitializeAsync()
    {
        if (_initialized) return;

        try
        {
            // 5 秒超时
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            await _supabase.InitializeAsync().WaitAsync(cts.Token);

            // 尝试从 localStorage 恢复会话
            await RestoreSessionAsync();

            _initialized = true;
        }
        catch (OperationCanceledException)
        {
            Console.WriteLine("警告: Supabase 初始化超时，以离线模式运行");
            _initialized = true;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"警告: Supabase 初始化失败 - {ex.Message}");
            _initialized = true;
        }
    }

    /// <summary>
    /// 从 localStorage 恢复会话
    /// </summary>
    private async Task RestoreSessionAsync()
    {
        try
        {
            var sessionJson = await _js.InvokeAsync<string?>("localStorage.getItem", SessionKey);
            if (!string.IsNullOrEmpty(sessionJson))
            {
                var session = System.Text.Json.JsonSerializer.Deserialize<StoredSession>(sessionJson);
                if (session != null && !string.IsNullOrEmpty(session.AccessToken))
                {
                    // 使用 refresh token 恢复会话
                    var restored = await _supabase.Auth.SetSession(session.AccessToken, session.RefreshToken ?? "");
                    if (restored?.User != null)
                    {
                        UpdateCurrentUser(restored.User);
                        // 保存刷新后的 token
                        await SaveSessionAsync(restored);
                        return;
                    }
                }
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"恢复会话失败: {ex.Message}");
            // 清除无效的会话
            try { await _js.InvokeVoidAsync("localStorage.removeItem", SessionKey); } catch { }
        }
    }

    /// <summary>
    /// 保存会话到 localStorage
    /// </summary>
    private async Task SaveSessionAsync(Session session)
    {
        try
        {
            var stored = new StoredSession
            {
                AccessToken = session.AccessToken,
                RefreshToken = session.RefreshToken,
                ExpiresAt = session.ExpiresAt()
            };
            var json = System.Text.Json.JsonSerializer.Serialize(stored);
            await _js.InvokeVoidAsync("localStorage.setItem", SessionKey, json);
        }
        catch { }
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
                // 保存会话
                var session = _supabase.Auth.CurrentSession;
                if (session != null)
                {
                    await SaveSessionAsync(session);
                }
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
            // 清除 localStorage
            await _js.InvokeVoidAsync("localStorage.removeItem", SessionKey);
        }
        catch { }
        finally
        {
            _currentUser = new CurrentUser();
            OnAuthStateChanged?.Invoke();
        }
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

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    /// <summary>
    /// 存储的会话信息
    /// </summary>
    private class StoredSession
    {
        public string? AccessToken { get; set; }
        public string? RefreshToken { get; set; }
        public DateTime? ExpiresAt { get; set; }
    }
}
