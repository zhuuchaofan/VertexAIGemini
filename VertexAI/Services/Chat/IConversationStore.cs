using VertexAI.Data.Entities;

namespace VertexAI.Services.Chat;

public interface IConversationStore
{
    Task<Conversation?> CreateConversationAsync(Guid userId, string presetId, string? customPrompt = null);

    Task<Message?> AddMessageAsync(
        Guid conversationId,
        Guid userId,
        string role,
        string content,
        string? thinkingContent = null);

    Task UpdateTokenCountAsync(Guid conversationId, Guid userId, int tokenCount);
}
