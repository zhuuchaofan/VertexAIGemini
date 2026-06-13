using VertexAI.Services.Chat;

namespace VertexAI.Services;

public sealed class MockProvider : IChatModelProvider
{
    private static readonly MockChatModelClient Catalog = new();

    public ChatProviderInfo Info { get; } = new(
        "mock",
        "Local Mock",
        "Deterministic local provider for frontend, API, and deployment smoke tests");

    public IReadOnlyList<ChatModelOption> ModelOptions => Catalog.ModelOptions;
    public IReadOnlyList<PromptPresetConfig> Presets => Catalog.Presets;
    public string DefaultModelName => Catalog.CurrentModelName;
    public string DefaultPresetId => Catalog.CurrentPresetId;

    public IChatModelClient CreateClient() => new MockChatModelClient();
}
