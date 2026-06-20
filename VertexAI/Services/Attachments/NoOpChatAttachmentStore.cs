using VertexAI.Services.Chat;

namespace VertexAI.Services.Attachments;

public sealed class NoOpChatAttachmentStore : IChatAttachmentStore
{
    public bool IsEnabled => false;

    public Task<ChatAttachment> SaveAsync(
        ChatAttachment attachment,
        Guid conversationId,
        Guid messageId,
        int index,
        CancellationToken cancellationToken = default) =>
        Task.FromResult(attachment);

    public Task<ChatAttachment> LoadAsync(
        ChatAttachment attachment,
        CancellationToken cancellationToken = default) =>
        Task.FromResult(attachment);

    public Task DeleteAsync(
        ChatAttachment attachment,
        CancellationToken cancellationToken = default) =>
        Task.CompletedTask;
}
