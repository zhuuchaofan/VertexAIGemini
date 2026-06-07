namespace VertexAI.Services.Chat;

public sealed record ChatImageAttachment(
    string Base64Data,
    string MimeType,
    string? FileName);
