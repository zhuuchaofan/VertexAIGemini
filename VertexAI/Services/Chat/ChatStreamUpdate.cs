namespace VertexAI.Services.Chat;

public sealed record ChatStreamUpdate(
    string Content,
    string? ThinkingContent,
    List<SearchCitation>? Citations = null);
