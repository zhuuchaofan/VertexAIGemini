namespace VertexAI.Services.Chat;

public interface IChatRequestAugmenter
{
    Task<ChatRequestAugmentation> AugmentAsync(
        ChatRequestContext context,
        ChatRequestAugmentation current,
        CancellationToken cancellationToken = default);
}
