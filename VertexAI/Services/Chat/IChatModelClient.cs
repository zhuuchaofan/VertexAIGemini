namespace VertexAI.Services.Chat;

public interface IChatModelClient
{
    int CurrentTokenCount { get; }
    string CurrentModelName { get; }
    string CurrentPresetId { get; }
    string CurrentCustomPrompt { get; }
    IReadOnlyList<ChatModelOption> ModelOptions { get; }
    IReadOnlyList<PromptPresetConfig> Presets { get; }

    Task ConfigureAsync(ChatSessionOptions? options);
    Task LoadHistoryAsync(IReadOnlyCollection<ChatHistoryEntry> messages);
    IAsyncEnumerable<ChatChunk> StreamChatAsync(ChatModelRequest request);
}
