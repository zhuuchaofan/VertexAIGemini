using VertexAI.Services.Auth;

namespace VertexAI.Services.Chat;

public interface IConversationStore
{
    Task<List<Conversation>> GetUserConversationsAsync(AuthenticatedUser user, int offset, int limit);

    Task<Conversation?> GetConversationAsync(Guid conversationId, AuthenticatedUser user);

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
        IReadOnlyCollection<ChatImageAttachment>? attachments = null);

    Task UpdateTitleAsync(Guid conversationId, AuthenticatedUser user, string title);

    Task DeleteConversationAsync(Guid conversationId, AuthenticatedUser user);

    Task UpdateTokenCountAsync(Guid conversationId, AuthenticatedUser user, int tokenCount);
}
