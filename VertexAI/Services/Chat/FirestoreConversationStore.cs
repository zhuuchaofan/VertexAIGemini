using System.Text.Json;
using Google.Cloud.Firestore;
using VertexAI.Data.Entities;
using VertexAI.Services.Auth;
using VertexAI.Services.Firestore;

namespace VertexAI.Services.Chat;

public sealed class FirestoreConversationStore : IConversationStore
{
    private const string ConversationsCollection = "conversations";
    private const string MessagesCollection = "messages";

    private readonly FirestoreDb _db;

    public FirestoreConversationStore(FirestoreDb db)
    {
        _db = db;
    }

    public async Task<List<Conversation>> GetUserConversationsAsync(
        AuthenticatedUser user,
        int offset,
        int limit)
    {
        var snapshot = await _db.Collection(ConversationsCollection)
            .WhereEqualTo("uid", GetUid(user))
            .OrderByDescending("updatedAt")
            .Offset(Math.Max(0, offset))
            .Limit(Math.Max(1, limit))
            .GetSnapshotAsync();

        return snapshot.Documents
            .Select(ToConversation)
            .Where(conversation => conversation != null)
            .Cast<Conversation>()
            .ToList();
    }

    public async Task<Conversation?> GetConversationAsync(Guid conversationId, AuthenticatedUser user)
    {
        var conversationSnapshot = await GetConversationDocument(conversationId).GetSnapshotAsync();
        var conversation = ToConversation(conversationSnapshot);
        if (conversation == null || !OwnsConversation(conversationSnapshot, user))
        {
            return null;
        }

        var messagesSnapshot = await _db.Collection(MessagesCollection)
            .WhereEqualTo("uid", GetUid(user))
            .WhereEqualTo("conversationId", conversationId.ToString("D"))
            .OrderBy("createdAt")
            .GetSnapshotAsync();

        conversation.Messages = messagesSnapshot.Documents
            .Select(ToMessage)
            .Where(message => message != null)
            .Cast<Message>()
            .ToList();

        return conversation;
    }

    public async Task<Conversation?> CreateConversationAsync(
        AuthenticatedUser user,
        string providerId,
        string modelName,
        string presetId,
        string? customPrompt = null)
    {
        var conversation = new Conversation
        {
            UserId = user.LocalUserId,
            ProviderId = providerId,
            ModelName = modelName,
            PresetId = presetId,
            CustomPrompt = customPrompt
        };

        await GetConversationDocument(conversation.Id).SetAsync(new Dictionary<string, object?>
        {
            ["uid"] = GetUid(user),
            ["localUserId"] = user.LocalUserId.ToString("D"),
            ["title"] = conversation.Title,
            ["providerId"] = providerId,
            ["modelName"] = modelName,
            ["presetId"] = presetId,
            ["customPrompt"] = customPrompt,
            ["historySummary"] = null,
            ["tokenCount"] = 0,
            ["createdAt"] = Timestamp.FromDateTime(conversation.CreatedAt),
            ["updatedAt"] = Timestamp.FromDateTime(conversation.UpdatedAt)
        });

        return conversation;
    }

    public async Task<IReadOnlyList<ChatHistoryEntry>> GetHistoryAsync(
        Guid conversationId,
        AuthenticatedUser user,
        int maxMessages)
    {
        var conversationSnapshot = await GetConversationDocument(conversationId).GetSnapshotAsync();
        if (!OwnsConversation(conversationSnapshot, user))
        {
            return [];
        }

        var take = Math.Max(1, maxMessages);
        var snapshot = await _db.Collection(MessagesCollection)
            .WhereEqualTo("uid", GetUid(user))
            .WhereEqualTo("conversationId", conversationId.ToString("D"))
            .OrderByDescending("createdAt")
            .Limit(take)
            .GetSnapshotAsync();

        return snapshot.Documents
            .Select(ToMessage)
            .Where(message => message != null)
            .Cast<Message>()
            .OrderBy(message => message.CreatedAt)
            .Select(message => new ChatHistoryEntry(
                message.Role,
                message.Content,
                message.ThinkingContent,
                DeserializeAttachments(message.AttachmentsJson)))
            .ToList();
    }

    public async Task<Message?> AddMessageAsync(
        Guid conversationId,
        AuthenticatedUser user,
        string role,
        string content,
        string? thinkingContent = null,
        IReadOnlyCollection<ChatImageAttachment>? attachments = null)
    {
        var conversationRef = GetConversationDocument(conversationId);
        var conversationSnapshot = await conversationRef.GetSnapshotAsync();
        if (!OwnsConversation(conversationSnapshot, user))
        {
            return null;
        }

        var attachmentsJson = attachments is { Count: > 0 }
            ? JsonSerializer.Serialize(attachments)
            : null;
        var message = new Message
        {
            Id = Guid.NewGuid(),
            ConversationId = conversationId,
            Role = role,
            Content = content,
            ThinkingContent = thinkingContent,
            AttachmentsJson = attachmentsJson
        };

        await _db.Collection(MessagesCollection).Document(message.Id.ToString("D")).SetAsync(new Dictionary<string, object?>
        {
            ["uid"] = GetUid(user),
            ["localUserId"] = user.LocalUserId.ToString("D"),
            ["conversationId"] = conversationId.ToString("D"),
            ["role"] = role,
            ["content"] = content,
            ["thinkingContent"] = thinkingContent,
            ["attachmentsJson"] = attachmentsJson,
            ["createdAt"] = Timestamp.FromDateTime(message.CreatedAt)
        });

        var updates = new Dictionary<string, object?>
        {
            ["updatedAt"] = Timestamp.FromDateTime(DateTime.UtcNow)
        };

        if (role == "user"
            && (!conversationSnapshot.TryGetValue<string>("title", out var title)
                || string.IsNullOrWhiteSpace(title)))
        {
            updates["title"] = CreateTitle(content, attachments?.Count ?? 0);
        }

        await conversationRef.UpdateAsync(updates);
        return message;
    }

    public async Task UpdateTitleAsync(Guid conversationId, AuthenticatedUser user, string title)
    {
        var conversationRef = GetConversationDocument(conversationId);
        var snapshot = await conversationRef.GetSnapshotAsync();
        if (!OwnsConversation(snapshot, user))
        {
            return;
        }

        await conversationRef.UpdateAsync(new Dictionary<string, object>
        {
            ["title"] = title,
            ["updatedAt"] = Timestamp.FromDateTime(DateTime.UtcNow)
        });
    }

    public async Task DeleteConversationAsync(Guid conversationId, AuthenticatedUser user)
    {
        var conversationRef = GetConversationDocument(conversationId);
        var snapshot = await conversationRef.GetSnapshotAsync();
        if (!OwnsConversation(snapshot, user))
        {
            return;
        }

        var messages = await _db.Collection(MessagesCollection)
            .WhereEqualTo("uid", GetUid(user))
            .WhereEqualTo("conversationId", conversationId.ToString("D"))
            .GetSnapshotAsync();

        foreach (var message in messages.Documents)
        {
            await message.Reference.DeleteAsync();
        }

        await conversationRef.DeleteAsync();
    }

    public async Task UpdateTokenCountAsync(Guid conversationId, AuthenticatedUser user, int tokenCount)
    {
        var conversationRef = GetConversationDocument(conversationId);
        var snapshot = await conversationRef.GetSnapshotAsync();
        if (!OwnsConversation(snapshot, user))
        {
            return;
        }

        await conversationRef.UpdateAsync(new Dictionary<string, object>
        {
            ["tokenCount"] = tokenCount,
            ["updatedAt"] = Timestamp.FromDateTime(DateTime.UtcNow)
        });
    }

    private DocumentReference GetConversationDocument(Guid conversationId) =>
        _db.Collection(ConversationsCollection).Document(conversationId.ToString("D"));

    private static bool OwnsConversation(DocumentSnapshot snapshot, AuthenticatedUser user) =>
        snapshot.Exists
        && snapshot.TryGetValue<string>("uid", out var uid)
        && string.Equals(uid, GetUid(user), StringComparison.Ordinal);

    private static string GetUid(AuthenticatedUser user) =>
        FirestoreDocumentIds.UserDocumentId(user.LocalUserId, user.FirebaseUid);

    private static Conversation? ToConversation(DocumentSnapshot snapshot)
    {
        if (!snapshot.Exists || !Guid.TryParse(snapshot.Id, out var id))
        {
            return null;
        }

        return new Conversation
        {
            Id = id,
            UserId = ReadGuid(snapshot, "localUserId"),
            Title = ReadString(snapshot, "title"),
            ProviderId = ReadString(snapshot, "providerId") ?? "gemini",
            ModelName = ReadString(snapshot, "modelName") ?? "",
            PresetId = ReadString(snapshot, "presetId") ?? "default",
            CustomPrompt = ReadString(snapshot, "customPrompt"),
            HistorySummary = ReadString(snapshot, "historySummary"),
            TokenCount = ReadInt(snapshot, "tokenCount"),
            CreatedAt = ReadTimestamp(snapshot, "createdAt"),
            UpdatedAt = ReadTimestamp(snapshot, "updatedAt")
        };
    }

    private static Message? ToMessage(DocumentSnapshot snapshot)
    {
        if (!snapshot.Exists || !Guid.TryParse(snapshot.Id, out var id))
        {
            return null;
        }

        var conversationId = ReadString(snapshot, "conversationId");
        if (!Guid.TryParse(conversationId, out var parsedConversationId))
        {
            return null;
        }

        return new Message
        {
            Id = id,
            ConversationId = parsedConversationId,
            Role = ReadString(snapshot, "role") ?? "user",
            Content = ReadString(snapshot, "content") ?? "",
            ThinkingContent = ReadString(snapshot, "thinkingContent"),
            AttachmentsJson = ReadString(snapshot, "attachmentsJson"),
            CreatedAt = ReadTimestamp(snapshot, "createdAt")
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

    private static string CreateTitle(string content, int attachmentCount)
    {
        var fallbackTitle = attachmentCount > 0
            ? $"Image request ({attachmentCount})"
            : "Untitled";
        var titleSource = string.IsNullOrWhiteSpace(content) ? fallbackTitle : content;
        return titleSource.Length > 50 ? titleSource[..50] + "..." : titleSource;
    }

    private static IReadOnlyList<ChatImageAttachment> DeserializeAttachments(string? attachmentsJson)
    {
        if (string.IsNullOrWhiteSpace(attachmentsJson))
        {
            return [];
        }

        try
        {
            return JsonSerializer.Deserialize<List<ChatImageAttachment>>(attachmentsJson) ?? [];
        }
        catch (JsonException)
        {
            return [];
        }
    }
}
