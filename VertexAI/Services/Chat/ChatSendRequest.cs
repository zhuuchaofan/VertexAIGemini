using VertexAI.Services.Auth;

namespace VertexAI.Services.Chat;

public sealed record ChatSendRequest(
    Guid? ConversationId,
    AuthenticatedUser User,
    string Message,
    IReadOnlyCollection<ChatAttachment> Attachments,
    string SearchMode = SearchModes.Auto,
    ChatSessionOptions? Options = null);
