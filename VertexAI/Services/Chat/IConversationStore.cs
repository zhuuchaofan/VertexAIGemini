using VertexAI.Services.Auth;

namespace VertexAI.Services.Chat;

public interface IConversationStore
{
    Task<List<Conversation>> GetUserConversationsAsync(AuthenticatedUser user, int offset, int limit);

    Task<List<Conversation>> GetUserConversationsPageAsync(AuthenticatedUser user, string? cursor, int limit);

    /// <param name="includeAttachmentData">
    /// False reads only the attachments' details (name, type, size); the content stays in storage. An export that
    /// only lists attachments, or a page that does not show them, then does not download every file first.
    /// </param>
    Task<Conversation?> GetConversationAsync(Guid conversationId, AuthenticatedUser user, bool includeAttachmentData = true);

    Task<Conversation?> CreateConversationAsync(
        AuthenticatedUser user,
        string providerId,
        string modelName,
        string presetId,
        string? customPrompt = null);

    Task<IReadOnlyList<ChatHistoryEntry>> GetHistoryAsync(Guid conversationId, AuthenticatedUser user, int maxMessages);

    Task<Message?> AddMessageAsync(
        Guid conversationId,
        AuthenticatedUser user,
        string role,
        string content,
        string? thinkingContent = null,
        IReadOnlyCollection<ChatAttachment>? attachments = null);

    Task UpdateTitleAsync(Guid conversationId, AuthenticatedUser user, string title);

    /// <summary>The person's pinned conversations, most recently pinned first.</summary>
    Task<List<Conversation>> GetPinnedConversationsAsync(AuthenticatedUser user, int limit);

    /// <summary>
    /// Pins or unpins one of the person's conversations. Does not change when it was last updated, so pinning does
    /// not reorder the recent list. False when it is not theirs (or does not exist).
    /// </summary>
    Task<bool> SetPinnedAsync(Guid conversationId, AuthenticatedUser user, bool pinned);

    Task DeleteConversationAsync(Guid conversationId, AuthenticatedUser user);

    Task UpdateTokenCountAsync(Guid conversationId, AuthenticatedUser user, int tokenCount);
}
