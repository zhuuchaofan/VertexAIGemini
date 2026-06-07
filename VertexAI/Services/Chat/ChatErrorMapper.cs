namespace VertexAI.Services.Chat;

internal static class ChatErrorMapper
{
    public static string ToUserMessage(Exception exception)
    {
        if (exception is TaskCanceledException)
        {
            return "\u8bf7\u6c42\u8d85\u65f6\uff0c\u8bf7\u91cd\u8bd5";
        }

        if (exception is HttpRequestException)
        {
            return "\u7f51\u7edc\u8fde\u63a5\u5f02\u5e38\uff0c\u8bf7\u68c0\u67e5\u7f51\u7edc\u540e\u91cd\u8bd5";
        }

        return exception.Message switch
        {
            var message when Contains(message, "not configured") => "Vertex AI \u914d\u7f6e\u672a\u5b8c\u6210\uff0c\u8bf7\u5148\u68c0\u67e5 ProjectId \u548c\u6a21\u578b\u540d\u79f0",
            var message when Contains(message, "quota") => "API \u914d\u989d\u5df2\u7528\u5c3d\uff0c\u8bf7\u7a0d\u540e\u518d\u8bd5",
            var message when Contains(message, "Unavailable") => "AI \u670d\u52a1\u6682\u65f6\u4e0d\u53ef\u7528\uff0c\u8bf7\u7a0d\u540e\u91cd\u8bd5",
            var message when Contains(message, "permission") => "\u6743\u9650\u4e0d\u8db3\uff0c\u8bf7\u8054\u7cfb\u7ba1\u7406\u5458",
            _ => "\u670d\u52a1\u6682\u65f6\u4e0d\u53ef\u7528\uff0c\u8bf7\u7a0d\u540e\u91cd\u8bd5"
        };
    }

    private static bool Contains(string value, string text) =>
        value.Contains(text, StringComparison.OrdinalIgnoreCase);
}
