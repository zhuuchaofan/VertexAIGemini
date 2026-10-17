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

    /// <summary>
    /// Streams a reply and stops as soon as <paramref name="cancellationToken"/> is cancelled, including any
    /// auxiliary provider calls (token counting, history summaries). Implementations that cannot honor the token
    /// fall back to the uncancellable overload, so only clients used by runs need to override this.
    /// </summary>
    IAsyncEnumerable<ChatChunk> StreamChatAsync(ChatModelRequest request, CancellationToken cancellationToken) =>
        StreamChatAsync(request);
}
