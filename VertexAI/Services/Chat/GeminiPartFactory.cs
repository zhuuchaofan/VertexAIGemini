using Google.GenAI.Types;

namespace VertexAI.Services.Chat;

internal static class GeminiPartFactory
{
    public static List<Part> CreateParts(string message, IReadOnlyCollection<ChatAttachment> attachments)
    {
        var parts = new List<Part>();
        var trimmedMessage = message.Trim();

        if (!string.IsNullOrWhiteSpace(trimmedMessage))
        {
            parts.Add(new Part { Text = trimmedMessage });
        }

        foreach (var attachment in attachments)
        {
            parts.Add(new Part
            {
                InlineData = new Blob
                {
                    Data = Convert.FromBase64String(attachment.Base64Data),
                    MimeType = attachment.MimeType
                }
            });
        }

        return parts;
    }
}
