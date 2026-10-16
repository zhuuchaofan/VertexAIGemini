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
    /// <summary>
    /// Streams the reply. Implementations must stop and release the upstream request when
    /// <paramref name="cancellationToken"/> is cancelled, and must throw (never end silently) when the
    /// upstream stream breaks before a normal finish.
    /// </summary>
    IAsyncEnumerable<ChatChunk> StreamChatAsync(ChatModelRequest request, CancellationToken cancellationToken = default);
}
