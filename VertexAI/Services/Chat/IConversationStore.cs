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

    Task DeleteConversationAsync(Guid conversationId, AuthenticatedUser user);

    Task UpdateTokenCountAsync(Guid conversationId, AuthenticatedUser user, int tokenCount);
}
