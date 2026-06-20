namespace VertexAI.Services.Chat;

public sealed record ChatAttachment(
    string? Base64Data,
    string MimeType,
    string? FileName,
    string? StorageObjectName = null,
    long? SizeBytes = null);
