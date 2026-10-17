using VertexAI.Services.Chat;

namespace VertexAI.Services;

/// <summary>Local, credential-free provider. Only registered by the Development overrides.</summary>
public sealed class MockChatModelProvider : IChatModelProvider
{
    private readonly MockChatModelClient _sample = new();

    public ChatProviderInfo Info { get; } = new("mock", "Mock", "Deterministic local provider for development and tests");
    public IReadOnlyList<ChatModelOption> ModelOptions => _sample.ModelOptions;
    public IReadOnlyList<PromptPresetConfig> Presets => _sample.Presets;
    public string DefaultModelName => "mock-fast";
    public string DefaultPresetId => "default";
    public IChatModelClient CreateClient() => new MockChatModelClient();
}
