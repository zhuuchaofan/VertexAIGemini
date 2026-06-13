namespace VertexAI.Services.Chat;

public sealed record ChatHistoryEntry(
    string Role,
    string Content,
    string? ThinkingContent = null,
    IReadOnlyCollection<ChatImageAttachment>? Attachments = null);
