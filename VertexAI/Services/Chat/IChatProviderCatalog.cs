namespace VertexAI.Services.Chat;

public interface IChatProviderCatalog
{
    IReadOnlyList<IChatModelProvider> Providers { get; }
    string ResolveProviderId(string? providerId);
    IChatModelClient CreateClient(string? providerId);
}
