namespace VertexAI.Services.Chat;

internal static class ChatErrorMapper
{
    public static string ToUserMessage(Exception exception)
    {
        var providerMessage = ToProviderMessage(exception.Message);
        if (providerMessage != null)
        {
            return providerMessage;
        }

        if (exception is TaskCanceledException)
        {
            return "\u8bf7\u6c42\u8d85\u65f6\uff0c\u8bf7\u91cd\u8bd5";
        }

        if (exception is HttpRequestException)
        {
            return "\u7f51\u7edc\u8fde\u63a5\u5f02\u5e38\uff0c\u8bf7\u68c0\u67e5\u7f51\u7edc\u540e\u91cd\u8bd5";
        }

        return "\u670d\u52a1\u6682\u65f6\u4e0d\u53ef\u7528\uff0c\u8bf7\u7a0d\u540e\u91cd\u8bd5";
    }

    private static string? ToProviderMessage(string message) =>
        message switch
        {
            var value when Contains(value, "not configured") => "AI 服务端配置未完成，请检查 Cloud Run 环境变量",
            var value when Contains(value, "application default credentials") => "AI 服务账号凭据不可用，请检查 Cloud Run 运行时服务账号",
            var value when Contains(value, "credential file") => "AI 服务账号凭据不可用，请检查 Cloud Run 运行时服务账号",
            var value when Contains(value, "Access to the path") => "服务无法读取必要资源，请检查 Cloud Run 运行时权限",
            var value when Contains(value, "quota") => "API \u914d\u989d\u5df2\u7528\u5c3d\uff0c\u8bf7\u7a0d\u540e\u518d\u8bd5",
            var value when Contains(value, "Unavailable") => "AI \u670d\u52a1\u6682\u65f6\u4e0d\u53ef\u7528\uff0c\u8bf7\u7a0d\u540e\u91cd\u8bd5",
            var value when Contains(value, "firestore") && Contains(value, "permission") => "会话存储权限不足，请检查 Cloud Run 服务账号的 Firestore 权限",
            var value when Contains(value, "storage") && Contains(value, "permission") => "附件存储权限不足，请检查 Cloud Run 服务账号的 Cloud Storage 权限",
            var value when Contains(value, "blocked by safety") => "当前预设或输入触发了模型安全过滤，请切换为默认助手或调整内容后重试",
            var value when Contains(value, "response was empty") => "AI 服务没有返回有效内容，请切换预设或稍后重试",
            var value when Contains(value, "permission") || Contains(value, "IAM_PERMISSION_DENIED") => "AI 服务权限不足，请检查 Cloud Run 服务账号的 Vertex AI 权限",
            _ => null
        };

    private static bool Contains(string value, string text) =>
        value.Contains(text, StringComparison.OrdinalIgnoreCase);
}
