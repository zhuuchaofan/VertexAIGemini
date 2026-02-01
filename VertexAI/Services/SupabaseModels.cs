namespace VertexAI.Services;

/// <summary>
/// Supabase 配置
/// </summary>
public class SupabaseSettings
{
    public string Url { get; set; } = "";
    public string Key { get; set; } = "";
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
