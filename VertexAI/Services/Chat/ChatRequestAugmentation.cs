namespace VertexAI.Services.Chat;

public sealed record ChatRequestAugmentation(
    string Message,
    IReadOnlyCollection<SearchCitation> Citations)
{
    public ChatRequestAugmentation(string message)
        : this(message, [])
    {
    }
}
