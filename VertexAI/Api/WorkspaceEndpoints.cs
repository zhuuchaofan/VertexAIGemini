using VertexAI.Services.Chat;

namespace VertexAI.Api;

public static class WorkspaceEndpoints
{
    public static void MapWorkspaceEndpoints(this WebApplication app)
    {
        var group = app.MapGroup("/api/workspace");

        group.MapGet("/config", GetConfig);
    }

    private static IResult GetConfig(IChatProviderCatalog catalog)
    {
        var providerConfigs = catalog.Providers.Select(provider =>
            new WorkspaceProviderConfig(
                provider.Info,
                provider.ModelOptions,
                provider.Presets,
                provider.DefaultPresetId,
                provider.DefaultModelName)).ToList();

        var defaultProviderId = catalog.ResolveProviderId(null);
        var defaultProvider = providerConfigs.First(p =>
            string.Equals(p.Provider.Id, defaultProviderId, StringComparison.OrdinalIgnoreCase));

        return Results.Ok(new WorkspaceConfigResponse(
            providerConfigs,
            defaultProviderId,
            defaultProvider.Models,
            defaultProvider.Presets,
            defaultProvider.DefaultPresetId,
            defaultProvider.DefaultModelName));
    }

    private sealed record WorkspaceConfigResponse(
        IReadOnlyList<WorkspaceProviderConfig> Providers,
        string DefaultProviderId,
        IReadOnlyList<ChatModelOption> Models,
        IReadOnlyList<PromptPresetConfig> Presets,
        string DefaultPresetId,
        string DefaultModelName);

    private sealed record WorkspaceProviderConfig(
        ChatProviderInfo Provider,
        IReadOnlyList<ChatModelOption> Models,
        IReadOnlyList<PromptPresetConfig> Presets,
        string DefaultPresetId,
        string DefaultModelName);
}
