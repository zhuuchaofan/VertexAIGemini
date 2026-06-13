using VertexAI.Data.Entities;

namespace VertexAI.Services.Chat;

public interface IConversationStore
{
    Task<Conversation?> CreateConversationAsync(
        Guid userId,
        string providerId,
        string modelName,
        string presetId,
        string? customPrompt = null);

    Task<IReadOnlyList<ChatHistoryEntry>> GetHistoryAsync(Guid conversationId, Guid userId);

    Task<Message?> AddMessageAsync(
        Guid conversationId,
        Guid userId,
        string role,
        string content,
        string? thinkingContent = null,
        IReadOnlyCollection<ChatImageAttachment>? attachments = null);

    Task UpdateTokenCountAsync(Guid conversationId, Guid userId, int tokenCount);
}
