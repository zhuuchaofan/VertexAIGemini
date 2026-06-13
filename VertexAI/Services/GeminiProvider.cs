using VertexAI.Services.Chat;
using Microsoft.Extensions.Options;

namespace VertexAI.Services;

public sealed class GeminiProvider : IChatModelProvider
{
    private readonly IServiceProvider _services;

    public GeminiProvider(IServiceProvider services, IOptions<GeminiSettings> settings)
    {
        _services = services;
        var config = settings.Value;
        ModelOptions = GeminiCatalog.CreateModelOptions(config);
        Presets = GeminiCatalog.CreatePresets(config);
        DefaultModelName = GeminiCatalog.ResolveModelName(config.ModelName, ModelOptions);
        DefaultPresetId = GeminiCatalog.ResolvePresetId(Presets);
    }

    public ChatProviderInfo Info { get; } = new(
        "gemini",
        "Google Gemini",
        "Google GenAI / Vertex AI Gemini provider");

    public IReadOnlyList<ChatModelOption> ModelOptions { get; }
    public IReadOnlyList<PromptPresetConfig> Presets { get; }
    public string DefaultModelName { get; }
    public string DefaultPresetId { get; }

    public IChatModelClient CreateClient() =>
        _services.GetRequiredService<GeminiService>();
}
