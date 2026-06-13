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
            var value when Contains(value, "not configured") => "Vertex AI 服务端配置未完成，请先检查 ProjectId 和模型名称",
            var value when Contains(value, "application default credentials") => "Vertex AI 凭据未正确配置，请检查 GOOGLE_APPLICATION_CREDENTIALS 指向的 JSON 文件",
            var value when Contains(value, "credential file") => "Vertex AI 凭据文件无法读取，请检查 GCP_KEY_PATH 是否指向真实 JSON 文件",
            var value when Contains(value, "Access to the path") => "Vertex AI 凭据文件无法读取，请检查容器挂载路径和文件权限",
            var value when Contains(value, "quota") => "API \u914d\u989d\u5df2\u7528\u5c3d\uff0c\u8bf7\u7a0d\u540e\u518d\u8bd5",
            var value when Contains(value, "Unavailable") => "AI \u670d\u52a1\u6682\u65f6\u4e0d\u53ef\u7528\uff0c\u8bf7\u7a0d\u540e\u91cd\u8bd5",
            var value when Contains(value, "permission") || Contains(value, "IAM_PERMISSION_DENIED") => "Vertex AI 权限不足，请为服务账号授予包含 aiplatform.endpoints.predict 的权限",
            _ => null
        };

    private static bool Contains(string value, string text) =>
        value.Contains(text, StringComparison.OrdinalIgnoreCase);
}
