using Microsoft.Extensions.Options;
using VertexAI.Services.Chat;

namespace VertexAI.Services;

public sealed class OpenAICompatibleProvider : IChatModelProvider
{
    private readonly IServiceProvider _services;

    public OpenAICompatibleProvider(
        IServiceProvider services,
        IOptions<OpenAICompatibleSettings> settings)
    {
        _services = services;
        var config = settings.Value;
        Info = new ChatProviderInfo(config.ProviderId, config.Name, config.Description);
        ModelOptions = OpenAICompatibleCatalog.CreateModelOptions(config);
        Presets = OpenAICompatibleCatalog.CreatePresets(config);
        DefaultModelName = OpenAICompatibleCatalog.ResolveModelName(config.ModelName, ModelOptions);
        DefaultPresetId = GeminiCatalog.ResolvePresetId(Presets);
    }

    public ChatProviderInfo Info { get; }
    public IReadOnlyList<ChatModelOption> ModelOptions { get; }
    public IReadOnlyList<PromptPresetConfig> Presets { get; }
    public string DefaultModelName { get; }
    public string DefaultPresetId { get; }

    public IChatModelClient CreateClient() =>
        _services.GetRequiredService<OpenAICompatibleChatModelClient>();
}
