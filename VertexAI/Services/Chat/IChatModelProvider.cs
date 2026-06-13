namespace VertexAI.Services.Chat;

public interface IChatModelProvider
{
    ChatProviderInfo Info { get; }
    IReadOnlyList<ChatModelOption> ModelOptions { get; }
    IReadOnlyList<PromptPresetConfig> Presets { get; }
    string DefaultModelName { get; }
    string DefaultPresetId { get; }
    IChatModelClient CreateClient();
}
