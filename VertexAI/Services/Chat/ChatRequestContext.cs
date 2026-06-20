using VertexAI.Services.Auth;

namespace VertexAI.Services.Chat;

public sealed record ChatRequestContext(
    Guid? ConversationId,
    AuthenticatedUser User,
    string OriginalMessage,
    IReadOnlyCollection<ChatAttachment> Attachments,
    string SearchMode,
    IReadOnlyList<ChatHistoryEntry> History,
    ChatSessionOptions? Options);
