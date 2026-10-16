namespace VertexAI.Configuration;

public sealed class ChatStreamSettings
{
    /// <summary>Seconds between SSE keep-alive comments (keeps proxies from closing an idle stream).</summary>
    public int HeartbeatSeconds { get; set; } = 15;

    /// <summary>
    /// Hard cap for one generation. It must stay below the Cloud Run request timeout of both services
    /// (300s by default) so the app can end the stream cleanly with a "failed" result before the
    /// platform cuts the connection.
    /// </summary>
    public int MaxGenerationSeconds { get; set; } = 240;
}
