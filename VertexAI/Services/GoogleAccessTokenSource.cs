using Google.Apis.Auth.OAuth2;

namespace VertexAI.Services;

/// <summary>A bearer token for Google APIs, from the identity the service runs as.</summary>
public interface IGoogleAccessTokenSource
{
    Task<string> GetAsync(CancellationToken cancellationToken);
}

/// <summary>
/// Application Default Credentials (the Cloud Run service account). The credential caches and refreshes the token
/// itself, so asking for one on every request is cheap.
/// </summary>
public sealed class GoogleAdcAccessTokenSource : IGoogleAccessTokenSource
{
    public static readonly GoogleAdcAccessTokenSource Shared = new();

    private readonly Lazy<Task<ICredential>> _credential = new(async () =>
        (await GoogleCredential.GetApplicationDefaultAsync()).CreateScoped("https://www.googleapis.com/auth/cloud-platform"));

    public async Task<string> GetAsync(CancellationToken cancellationToken)
    {
        var credential = await _credential.Value;
        return await credential.GetAccessTokenForRequestAsync(cancellationToken: cancellationToken);
    }
}
