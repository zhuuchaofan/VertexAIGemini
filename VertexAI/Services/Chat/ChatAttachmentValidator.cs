namespace VertexAI.Services.Chat;

public static class ChatAttachmentValidator
{
    private const int MaxAttachments = 8;
    private const int MaxImageBytes = 4 * 1024 * 1024;
    private const int MaxFileBytes = 512 * 1024;

    private static readonly HashSet<string> AllowedImageMimeTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "image/jpeg",
        "image/png",
        "image/webp",
        "image/gif"
    };

    private static readonly HashSet<string> AllowedFileMimeTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "application/pdf",
        "application/json",
        "application/xml",
        "application/javascript",
        "application/x-yaml",
        "text/csv",
        "text/html",
        "text/markdown",
        "text/plain",
        "text/xml",
        "text/yaml"
    };

    public static string? Validate(IReadOnlyCollection<ChatAttachment> attachments)
    {
        if (attachments.Count > MaxAttachments)
        {
            return $"最多支持 {MaxAttachments} 个附件。";
        }

        foreach (var attachment in attachments)
        {
            if (!IsAllowedMimeType(attachment.MimeType))
            {
                return $"不支持的附件格式: {attachment.MimeType}";
            }

            if (string.IsNullOrWhiteSpace(attachment.Base64Data))
            {
                return "附件内容不能为空。";
            }

            if (!TryGetDecodedLength(attachment.Base64Data, out var decodedLength))
            {
                return "附件 Base64 内容无效。";
            }

            var maxBytes = IsImage(attachment.MimeType) ? MaxImageBytes : MaxFileBytes;
            if (decodedLength > maxBytes)
            {
                return IsImage(attachment.MimeType)
                    ? "图片过大，单张最大支持 4MB。"
                    : "文件过大，单个最大支持 512KB。";
            }
        }

        return null;
    }

    private static bool IsAllowedMimeType(string mimeType)
    {
        if (string.IsNullOrWhiteSpace(mimeType))
        {
            return false;
        }

        return AllowedImageMimeTypes.Contains(mimeType)
            || AllowedFileMimeTypes.Contains(mimeType)
            || mimeType.StartsWith("text/", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsImage(string mimeType) =>
        mimeType.StartsWith("image/", StringComparison.OrdinalIgnoreCase);

    private static bool TryGetDecodedLength(string base64, out int decodedLength)
    {
        decodedLength = 0;

        try
        {
            var buffer = new Span<byte>(new byte[base64.Length]);
            if (!Convert.TryFromBase64String(base64, buffer, out decodedLength))
            {
                return false;
            }

            return true;
        }
        catch (FormatException)
        {
            return false;
        }
    }
}
