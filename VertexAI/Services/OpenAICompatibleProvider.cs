using VertexAI.Services.Chat;

namespace VertexAI.Services;

public sealed class OpenAICompatibleProvider : IChatModelProvider
{
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly OpenAICompatibleProviderSettings _settings;
    private readonly IGoogleAccessTokenSource? _tokenSource;

    public OpenAICompatibleProvider(
        IHttpClientFactory httpClientFactory,
        OpenAICompatibleProviderSettings settings,
        IGoogleAccessTokenSource? tokenSource = null)
    {
        _httpClientFactory = httpClientFactory;
        _settings = settings;
        _tokenSource = tokenSource;
        Info = new ChatProviderInfo(settings.ProviderId, settings.Name, settings.Description);
        ModelOptions = OpenAICompatibleCatalog.CreateModelOptions(settings);
        Presets = OpenAICompatibleCatalog.CreatePresets(settings);
        DefaultModelName = OpenAICompatibleCatalog.ResolveModelName(settings.ModelName, ModelOptions);
        DefaultPresetId = GeminiCatalog.ResolvePresetId(Presets);
    }

    public ChatProviderInfo Info { get; }
    public IReadOnlyList<ChatModelOption> ModelOptions { get; }
    public IReadOnlyList<PromptPresetConfig> Presets { get; }
    public string DefaultModelName { get; }
    public string DefaultPresetId { get; }

    public IChatModelClient CreateClient() =>
        new OpenAICompatibleChatModelClient(
            _httpClientFactory.CreateClient(nameof(OpenAICompatibleChatModelClient)),
            _settings,
            _tokenSource);
}
