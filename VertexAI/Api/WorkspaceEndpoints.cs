using Microsoft.Extensions.Options;
using VertexAI.Configuration;
using VertexAI.Services.Chat;

namespace VertexAI.Api;

public static class WorkspaceEndpoints
{
    public static void MapWorkspaceEndpoints(this WebApplication app)
    {
        var group = app.MapGroup("/api/workspace");

        group.MapGet("/config", GetConfig);
    }

    private static IResult GetConfig(
        IChatProviderCatalog catalog,
        IOptions<FirebaseSettings> firebaseOptions)
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
            defaultProvider.DefaultModelName,
            CreateFirebaseConfig(firebaseOptions.Value)));
    }

    private static FirebaseClientConfig? CreateFirebaseConfig(FirebaseSettings settings)
    {
        var projectId = Environment.GetEnvironmentVariable("FIREBASE_PROJECT_ID")
            ?? settings.ProjectId
            ?? Environment.GetEnvironmentVariable("PROJECT_ID");
        var apiKey = Environment.GetEnvironmentVariable("FIREBASE_API_KEY")
            ?? settings.ApiKey;
        var authDomain = Environment.GetEnvironmentVariable("FIREBASE_AUTH_DOMAIN")
            ?? settings.AuthDomain;
        var appId = Environment.GetEnvironmentVariable("FIREBASE_APP_ID")
            ?? settings.AppId;

        if (string.IsNullOrWhiteSpace(projectId) || string.IsNullOrWhiteSpace(apiKey))
        {
            return null;
        }

        return new FirebaseClientConfig(
            apiKey,
            authDomain,
            projectId,
            appId);
    }

    private sealed record WorkspaceConfigResponse(
        IReadOnlyList<WorkspaceProviderConfig> Providers,
        string DefaultProviderId,
        IReadOnlyList<ChatModelOption> Models,
        IReadOnlyList<PromptPresetConfig> Presets,
        string DefaultPresetId,
        string DefaultModelName,
        FirebaseClientConfig? Firebase);

    private sealed record FirebaseClientConfig(
        string ApiKey,
        string? AuthDomain,
        string ProjectId,
        string? AppId);

    private sealed record WorkspaceProviderConfig(
        ChatProviderInfo Provider,
        IReadOnlyList<ChatModelOption> Models,
        IReadOnlyList<PromptPresetConfig> Presets,
        string DefaultPresetId,
        string DefaultModelName);
}
