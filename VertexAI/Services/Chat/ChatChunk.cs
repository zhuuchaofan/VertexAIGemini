namespace VertexAI.Services.Chat;

public sealed record ChatChunk
{
    public required string Text { get; init; }
    public bool IsThinking { get; init; }
    public List<SearchCitation>? Citations { get; init; }
}
