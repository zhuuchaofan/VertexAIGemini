using Google.Cloud.Storage.V1;
using Google;
using Microsoft.Extensions.Options;
using VertexAI.Configuration;
using VertexAI.Services.Chat;

namespace VertexAI.Services.Attachments;

public sealed class CloudStorageChatAttachmentStore : IChatAttachmentStore
{
    private readonly StorageClient _storage;
    private readonly string _bucket;

    public CloudStorageChatAttachmentStore(StorageClient storage, IOptions<PersistenceSettings> settings)
    {
        _storage = storage;
        _bucket = Environment.GetEnvironmentVariable("ATTACHMENT_STORAGE_BUCKET")
            ?? settings.Value.AttachmentStorageBucket
            ?? "";
    }

    public bool IsEnabled => !string.IsNullOrWhiteSpace(_bucket);

    public async Task<ChatAttachment> SaveAsync(
        ChatAttachment attachment,
        Guid conversationId,
        Guid messageId,
        int index,
        CancellationToken cancellationToken = default)
    {
        if (!IsEnabled || string.IsNullOrWhiteSpace(attachment.Base64Data))
        {
            return attachment;
        }

        var bytes = Convert.FromBase64String(attachment.Base64Data);
        var objectName = CreateObjectName(conversationId, messageId, index, attachment);
        using var stream = new MemoryStream(bytes);

        await _storage.UploadObjectAsync(
            _bucket,
            objectName,
            attachment.MimeType,
            stream,
            cancellationToken: cancellationToken);

        return attachment with
        {
            Base64Data = null,
            StorageObjectName = objectName,
            SizeBytes = bytes.Length
        };
    }

    public async Task<ChatAttachment> LoadAsync(
        ChatAttachment attachment,
        CancellationToken cancellationToken = default)
    {
        if (!IsEnabled
            || !string.IsNullOrWhiteSpace(attachment.Base64Data)
            || string.IsNullOrWhiteSpace(attachment.StorageObjectName))
        {
            return attachment;
        }

        using var stream = new MemoryStream();
        await _storage.DownloadObjectAsync(
            _bucket,
            attachment.StorageObjectName,
            stream,
            cancellationToken: cancellationToken);

        var bytes = stream.ToArray();
        if (!string.IsNullOrWhiteSpace(attachment.Sha256)
            && !string.Equals(AttachmentNames.Sha256Hex(bytes), attachment.Sha256, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException($"Attachment object {attachment.StorageObjectName} does not match its recorded hash.");
        }

        return attachment with { Base64Data = Convert.ToBase64String(bytes) };
    }

    public async Task<bool> SaveCreateOnlyAsync(
        string objectName,
        string mimeType,
        byte[] bytes,
        CancellationToken cancellationToken = default)
    {
        using var stream = new MemoryStream(bytes);
        try
        {
            // IfGenerationMatch = 0 means "only if the object does not exist": a second writer gets 412 instead of overwriting.
            await _storage.UploadObjectAsync(
                _bucket,
                objectName,
                mimeType,
                stream,
                new UploadObjectOptions { IfGenerationMatch = 0 },
                cancellationToken);
            return true;
        }
        catch (GoogleApiException ex) when (ex.HttpStatusCode == System.Net.HttpStatusCode.PreconditionFailed)
        {
            var existing = await _storage.GetObjectAsync(_bucket, objectName, cancellationToken: cancellationToken);
            if (existing.Size == (ulong)bytes.Length) return false;
            throw new InvalidOperationException($"Attachment object {objectName} already exists with different content.");
        }
    }

    public async Task DeleteAsync(
        ChatAttachment attachment,
        CancellationToken cancellationToken = default)
    {
        if (!IsEnabled || string.IsNullOrWhiteSpace(attachment.StorageObjectName))
        {
            return;
        }

        try
        {
            await _storage.DeleteObjectAsync(
                _bucket,
                attachment.StorageObjectName,
                cancellationToken: cancellationToken);
        }
        catch (GoogleApiException ex) when (ex.HttpStatusCode == System.Net.HttpStatusCode.NotFound)
        {
        }
    }

    private static string CreateObjectName(
        Guid conversationId,
        Guid messageId,
        int index,
        ChatAttachment attachment)
    {
        var extension = AttachmentNames.Extension(attachment.FileName, attachment.MimeType);
        return string.Join('/',
            "chat-attachments",
            conversationId.ToString("D"),
            messageId.ToString("D"),
            $"{index:D2}{extension}");
    }
}
