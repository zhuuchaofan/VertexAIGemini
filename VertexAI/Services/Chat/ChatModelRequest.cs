namespace VertexAI.Services.Chat;

public sealed record ChatModelRequest(
    string Message,
    IReadOnlyCollection<ChatAttachment> Attachments,
    bool EnableSearch = false);
