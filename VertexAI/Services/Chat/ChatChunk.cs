namespace VertexAI.Services.Chat;

public sealed record ChatChunk
{
    public required string Text { get; init; }
    public bool IsThinking { get; init; }
    public List<SearchCitation>? Citations { get; init; }

    /// <summary>A picture the model made, as base64 in <see cref="ChatAttachment.Base64Data"/>. Never a draft from its thinking.</summary>
    public ChatAttachment? Image { get; init; }
}
