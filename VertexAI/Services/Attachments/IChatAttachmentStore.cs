using VertexAI.Services.Chat;

namespace VertexAI.Services.Attachments;

public interface IChatAttachmentStore
{
    bool IsEnabled { get; }

    Task<ChatAttachment> SaveAsync(
        ChatAttachment attachment,
        Guid conversationId,
        Guid messageId,
        int index,
        CancellationToken cancellationToken = default);

    Task<ChatAttachment> LoadAsync(
        ChatAttachment attachment,
        CancellationToken cancellationToken = default);

    Task DeleteAsync(
        ChatAttachment attachment,
        CancellationToken cancellationToken = default);
}
