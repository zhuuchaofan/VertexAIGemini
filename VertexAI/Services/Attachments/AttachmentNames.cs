using System.Security.Cryptography;
using VertexAI.Services.Chat;

namespace VertexAI.Services.Attachments;

/// <summary>
/// Object names carry a prefix of the content hash, so two different contents can never collide on one name and an
/// existing object with the same name is, for practical purposes, the same bytes.
/// </summary>
public static class AttachmentNames
{
    public static string Sha256Hex(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

    public static string ObjectName(Guid conversationId, Guid messageId, int index, string? fileName, string mimeType, string sha256) =>
        string.Join('/',
            "chat-attachments",
            conversationId.ToString("D"),
            messageId.ToString("D"),
            $"{index:D2}-{sha256[..16]}{Extension(fileName, mimeType)}");

    public static string Extension(string? fileName, string mimeType)
    {
        var fileExtension = Path.GetExtension(fileName);
        if (!string.IsNullOrWhiteSpace(fileExtension) && fileExtension.Length <= 12 && fileExtension.All(c => char.IsAsciiLetterOrDigit(c) || c == '.'))
        {
            return fileExtension.ToLowerInvariant();
        }

        return mimeType.ToLowerInvariant() switch
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

    /// <summary>A display name without any path, control characters or excessive length.</summary>
    public static string CleanFileName(string? fileName)
    {
        var name = Path.GetFileName((fileName ?? "").Replace('\\', '/'));
        name = new string(name.Where(c => !char.IsControl(c)).ToArray()).Trim();
        if (name.Length == 0) return "attachment";
        return name.Length <= 120 ? name : name[..120];
    }
}
