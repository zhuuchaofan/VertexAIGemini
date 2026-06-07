namespace VertexAI.Services.Chat;

public sealed record ChatSendResult(
    Guid? ConversationId,
    string Content,
    string? ThinkingContent,
    bool Succeeded,
    string? ErrorMessage);
