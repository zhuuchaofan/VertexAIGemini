using Google.GenAI.Types;

namespace VertexAI.Services;

/// <summary>
/// HTTP behavior shared by every Gemini SDK client.
/// <list type="bullet">
/// <item>One upstream request per logical call. A transport-level retry would turn one generation into
/// several billable provider calls that the run lifecycle (which promises at most one) could not see.</item>
/// <item>Cancelling closes the provider connection at once. .NET keeps an HTTP/1.1 connection open for
/// <see cref="SocketsHttpHandler.ResponseDrainTimeout"/> (2 seconds by default) while it drains an unread
/// response, so the provider would keep generating, and billing, after the user pressed stop.</item>
/// </list>
/// </summary>
public static class GeminiHttpOptions
{
    // Shared across clients (GeminiService is created per request) so connections are pooled.
    private static readonly SocketsHttpHandler SharedHandler = new()
    {
        ResponseDrainTimeout = TimeSpan.Zero,
        PooledConnectionLifetime = TimeSpan.FromMinutes(5)
    };

    public static HttpOptions Create(string? baseUrl = null) => new()
    {
        BaseUrl = baseUrl,
        RetryOptions = new HttpRetryOptions { Attempts = 1 }
    };

    public static ClientOptions CreateClientOptions() => new()
    {
        // The handler is shared, so disposing a client must not dispose it.
        HttpClientFactory = () => new HttpClient(SharedHandler, disposeHandler: false)
    };
}
