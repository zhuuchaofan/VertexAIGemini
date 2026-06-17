using VertexAI.Services.Auth;

namespace VertexAI.Services.Chat;

public sealed record ChatSendRequest(
    Guid? ConversationId,
    AuthenticatedUser User,
    string Message,
    IReadOnlyCollection<ChatImageAttachment> Images,
    bool EnableSearch = false,
    ChatSessionOptions? Options = null);
