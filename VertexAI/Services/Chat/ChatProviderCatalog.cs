using Microsoft.Extensions.Options;
using VertexAI.Configuration;

namespace VertexAI.Services.Chat;

public sealed class ChatProviderCatalog : IChatProviderCatalog
{
    private readonly IReadOnlyList<IChatModelProvider> _providers;
    private readonly string _defaultProviderId;

    public ChatProviderCatalog(
        IEnumerable<IChatModelProvider> providers,
        IOptions<WorkspaceSettings>? workspaceSettings = null)
    {
        _providers = providers.ToList();
        _defaultProviderId = workspaceSettings?.Value.DefaultProviderId ?? "gemini";

        if (_providers.Count == 0)
        {
            throw new InvalidOperationException("At least one chat model provider must be registered.");
        }
    }

    public IReadOnlyList<IChatModelProvider> Providers => _providers;

    public string ResolveProviderId(string? providerId) =>
        ResolveProvider(providerId).Info.Id;

    public IChatModelClient CreateClient(string? providerId)
    {
        return ResolveProvider(providerId).CreateClient();
    }

    private IChatModelProvider ResolveProvider(string? providerId)
    {
        if (string.IsNullOrWhiteSpace(providerId))
        {
            return string.IsNullOrWhiteSpace(_defaultProviderId)
                ? _providers[0]
                : _providers.FirstOrDefault(p => string.Equals(p.Info.Id, _defaultProviderId, StringComparison.OrdinalIgnoreCase))
                    ?? _providers[0];
        }

        return _providers.FirstOrDefault(p => string.Equals(p.Info.Id, providerId, StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidOperationException($"Unknown chat model provider '{providerId}'.");
    }
}
