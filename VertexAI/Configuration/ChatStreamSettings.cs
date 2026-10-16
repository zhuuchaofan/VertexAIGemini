namespace VertexAI.Configuration;

public sealed class ChatStreamSettings
{
    /// <summary>Seconds between SSE keep-alive comments (keeps proxies from closing an idle stream).</summary>
    public int HeartbeatSeconds { get; set; } = 15;

    /// <summary>
    /// Hard cap for one generation. The API's Cloud Run timeout must exceed it by at least 30s (time to
    /// save the partial reply, settle quota and send the final event); the deploy script enforces this.
    /// </summary>
    public int MaxGenerationSeconds { get; set; } = 240;
}
