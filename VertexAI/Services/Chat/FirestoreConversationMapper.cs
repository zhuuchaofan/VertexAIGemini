using Google.Cloud.Firestore;

namespace VertexAI.Services.Chat;

internal static class FirestoreConversationMapper
{
    public static Dictionary<string, object?> ToDocument(
        Conversation conversation,
        string uid)
    {
        return new Dictionary<string, object?>
        {
            [FirestoreConversationSchema.Uid] = uid,
            [FirestoreConversationSchema.LocalUserId] = conversation.UserId.ToString("D"),
            [FirestoreConversationSchema.Title] = conversation.Title,
            [FirestoreConversationSchema.ProviderId] = conversation.ProviderId,
            [FirestoreConversationSchema.ModelName] = conversation.ModelName,
            [FirestoreConversationSchema.PresetId] = conversation.PresetId,
            [FirestoreConversationSchema.CustomPrompt] = conversation.CustomPrompt,
            [FirestoreConversationSchema.HistorySummary] = conversation.HistorySummary,
            [FirestoreConversationSchema.TokenCount] = conversation.TokenCount,
            [FirestoreConversationSchema.CreatedAt] = Timestamp.FromDateTime(conversation.CreatedAt),
            [FirestoreConversationSchema.UpdatedAt] = Timestamp.FromDateTime(conversation.UpdatedAt)
        };
    }

    public static Dictionary<string, object?> ToDocument(
        Message message,
        string uid,
        Guid localUserId)
    {
        return new Dictionary<string, object?>
        {
            [FirestoreConversationSchema.Uid] = uid,
            [FirestoreConversationSchema.LocalUserId] = localUserId.ToString("D"),
            [FirestoreConversationSchema.ConversationId] = message.ConversationId.ToString("D"),
            [FirestoreConversationSchema.Role] = message.Role,
            [FirestoreConversationSchema.Content] = message.Content,
            [FirestoreConversationSchema.ThinkingContent] = message.ThinkingContent,
            [FirestoreConversationSchema.AttachmentsJson] = message.AttachmentsJson,
            [FirestoreConversationSchema.CreatedAt] = Timestamp.FromDateTime(message.CreatedAt)
        };
    }

    public static Conversation? ToConversation(DocumentSnapshot snapshot)
    {
        if (!snapshot.Exists || !Guid.TryParse(snapshot.Id, out var id))
        {
            return null;
        }

        return new Conversation
        {
            Id = id,
            UserId = ReadGuid(snapshot, FirestoreConversationSchema.LocalUserId),
            Title = ReadString(snapshot, FirestoreConversationSchema.Title),
            ProviderId = ReadString(snapshot, FirestoreConversationSchema.ProviderId) ?? "gemini",
            ModelName = ReadString(snapshot, FirestoreConversationSchema.ModelName) ?? "",
            PresetId = ReadString(snapshot, FirestoreConversationSchema.PresetId) ?? "default",
            CustomPrompt = ReadString(snapshot, FirestoreConversationSchema.CustomPrompt),
            HistorySummary = ReadString(snapshot, FirestoreConversationSchema.HistorySummary),
            TokenCount = ReadInt(snapshot, FirestoreConversationSchema.TokenCount),
            CreatedAt = ReadTimestamp(snapshot, FirestoreConversationSchema.CreatedAt),
            UpdatedAt = ReadTimestamp(snapshot, FirestoreConversationSchema.UpdatedAt)
        };
    }

    public static Message? ToMessage(DocumentSnapshot snapshot)
    {
        if (!snapshot.Exists || !Guid.TryParse(snapshot.Id, out var id))
        {
            return null;
        }

        var conversationId = ReadString(snapshot, FirestoreConversationSchema.ConversationId);
        if (!Guid.TryParse(conversationId, out var parsedConversationId))
        {
            return null;
        }

        return new Message
        {
            Id = id,
            ConversationId = parsedConversationId,
            Role = ReadString(snapshot, FirestoreConversationSchema.Role) ?? "user",
            Content = ReadString(snapshot, FirestoreConversationSchema.Content) ?? "",
            ThinkingContent = ReadString(snapshot, FirestoreConversationSchema.ThinkingContent),
            AttachmentsJson = ReadString(snapshot, FirestoreConversationSchema.AttachmentsJson),
            CreatedAt = ReadTimestamp(snapshot, FirestoreConversationSchema.CreatedAt)
        };
    }

    private static string? ReadString(DocumentSnapshot snapshot, string field) =>
        snapshot.TryGetValue<string>(field, out var value) ? value : null;

    private static int ReadInt(DocumentSnapshot snapshot, string field) =>
        snapshot.TryGetValue<long>(field, out var longValue)
            ? checked((int)longValue)
            : snapshot.TryGetValue<int>(field, out var intValue)
                ? intValue
                : 0;

    private static Guid ReadGuid(DocumentSnapshot snapshot, string field) =>
        Guid.TryParse(ReadString(snapshot, field), out var value) ? value : Guid.Empty;

    private static DateTime ReadTimestamp(DocumentSnapshot snapshot, string field) =>
        snapshot.TryGetValue<Timestamp>(field, out var timestamp)
            ? timestamp.ToDateTime()
            : DateTime.UtcNow;
}
