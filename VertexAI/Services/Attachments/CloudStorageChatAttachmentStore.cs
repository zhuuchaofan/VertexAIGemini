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

        return attachment with { Base64Data = Convert.ToBase64String(stream.ToArray()) };
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
        var extension = GetExtension(attachment);
        return string.Join('/',
            "chat-attachments",
            conversationId.ToString("D"),
            messageId.ToString("D"),
            $"{index:D2}{extension}");
    }

    private static string GetExtension(ChatAttachment attachment)
    {
        var fileExtension = Path.GetExtension(attachment.FileName);
        if (!string.IsNullOrWhiteSpace(fileExtension) && fileExtension.Length <= 12)
        {
            return fileExtension.ToLowerInvariant();
        }

        return attachment.MimeType.ToLowerInvariant() switch
        {
            "image/jpeg" => ".jpg",
            "image/png" => ".png",
            "image/webp" => ".webp",
            "image/gif" => ".gif",
            "application/pdf" => ".pdf",
            "application/json" => ".json",
            "text/plain" => ".txt",
            _ => ".bin"
        };
    }
}
