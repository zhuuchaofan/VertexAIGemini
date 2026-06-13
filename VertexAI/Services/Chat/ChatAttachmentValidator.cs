namespace VertexAI.Services.Chat;

public static class ChatAttachmentValidator
{
    private const int MaxImages = 5;
    private const int MaxImageBytes = 4 * 1024 * 1024;

    private static readonly HashSet<string> AllowedMimeTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "image/jpeg",
        "image/png",
        "image/webp",
        "image/gif"
    };

    public static string? Validate(IReadOnlyCollection<ChatImageAttachment> images)
    {
        if (images.Count > MaxImages)
        {
            return $"最多支持 {MaxImages} 张图片。";
        }

        foreach (var image in images)
        {
            if (string.IsNullOrWhiteSpace(image.MimeType) || !AllowedMimeTypes.Contains(image.MimeType))
            {
                return $"不支持的图片格式: {image.MimeType}";
            }

            if (string.IsNullOrWhiteSpace(image.Base64Data))
            {
                return "图片内容不能为空。";
            }

            if (!TryGetDecodedLength(image.Base64Data, out var decodedLength))
            {
                return "图片 Base64 内容无效。";
            }

            if (decodedLength > MaxImageBytes)
            {
                return "图片过大，单张最大支持 4MB。";
            }
        }

        return null;
    }

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
