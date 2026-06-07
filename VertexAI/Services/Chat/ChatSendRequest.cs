namespace VertexAI.Services.Chat;

public sealed record ChatSendRequest(
    Guid? ConversationId,
    Guid UserId,
    string Message,
    IReadOnlyCollection<ChatImageAttachment> Images,
    bool EnableSearch = false);
