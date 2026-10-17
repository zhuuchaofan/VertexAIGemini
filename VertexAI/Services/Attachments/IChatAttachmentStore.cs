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

    /// <summary>
    /// Writes the bytes under exactly this name, only if nothing is there yet. An existing object of the same size is
    /// the same upload arriving twice and counts as success; an existing object of another size is an error.
    /// Nothing is ever overwritten. Returns true when this call created the object.
    /// </summary>
    Task<bool> SaveCreateOnlyAsync(
        string objectName,
        string mimeType,
        byte[] bytes,
        CancellationToken cancellationToken = default);

    Task DeleteAsync(
        ChatAttachment attachment,
        CancellationToken cancellationToken = default);
}
