using System.Text.Json;

namespace VertexAI.Services.Chat;

internal static class ChatAttachmentSerializer
{
    public static string? Serialize(IReadOnlyCollection<ChatAttachment>? attachments) =>
        attachments is { Count: > 0 }
            ? JsonSerializer.Serialize(attachments)
            : null;

    public static IReadOnlyList<ChatAttachment> Deserialize(string? attachmentsJson)
    {
        if (string.IsNullOrWhiteSpace(attachmentsJson))
        {
            return [];
        }

        try
        {
            return JsonSerializer.Deserialize<List<ChatAttachment>>(attachmentsJson) ?? [];
        }
        catch (JsonException)
        {
            return [];
        }
    }
}
