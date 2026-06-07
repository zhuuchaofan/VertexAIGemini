using Google.GenAI.Types;

namespace VertexAI.Services.Chat;

internal static class GeminiPartFactory
{
    public static List<Part> CreateParts(string message, IReadOnlyCollection<ChatImageAttachment> images)
    {
        var parts = new List<Part>();
        var trimmedMessage = message.Trim();

        if (!string.IsNullOrWhiteSpace(trimmedMessage))
        {
            parts.Add(new Part { Text = trimmedMessage });
        }

        foreach (var image in images)
        {
            parts.Add(new Part
            {
                InlineData = new Blob
                {
                    Data = Convert.FromBase64String(image.Base64Data),
                    MimeType = image.MimeType
                }
            });
        }

        return parts;
    }
}
