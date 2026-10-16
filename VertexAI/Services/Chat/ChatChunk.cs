namespace VertexAI.Services.Chat;

public sealed record ChatChunk
{
    public required string Text { get; init; }
    public bool IsThinking { get; init; }
    public List<SearchCitation>? Citations { get; init; }

    /// <summary>Set on the last chunk when the model stopped because it hit its output limit.</summary>
    public bool IsTruncated { get; init; }
}
