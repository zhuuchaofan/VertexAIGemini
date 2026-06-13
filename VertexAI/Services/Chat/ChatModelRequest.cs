namespace VertexAI.Services.Chat;

public sealed record ChatModelRequest(
    string Message,
    IReadOnlyCollection<ChatImageAttachment> Images,
    bool EnableSearch = false);
