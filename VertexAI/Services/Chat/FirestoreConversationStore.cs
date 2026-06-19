using Google.Cloud.Firestore;
using VertexAI.Services.Auth;

namespace VertexAI.Services.Chat;

public sealed class FirestoreConversationStore : IConversationStore
{
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
        var uid = FirestoreConversationSchema.UserDocumentId(user);
        var snapshot = await _db.Collection(FirestoreConversationSchema.ConversationsCollection)
            .WhereEqualTo(FirestoreConversationSchema.Uid, uid)
            .OrderByDescending(FirestoreConversationSchema.UpdatedAt)
            .Offset(Math.Max(0, offset))
            .Limit(Math.Max(1, limit))
            .GetSnapshotAsync();

        return snapshot.Documents
            .Select(FirestoreConversationMapper.ToConversation)
            .Where(conversation => conversation != null)
            .Cast<Conversation>()
            .ToList();
    }

    public async Task<Conversation?> GetConversationAsync(Guid conversationId, AuthenticatedUser user)
    {
        var conversationSnapshot = await GetConversationDocument(conversationId).GetSnapshotAsync();
        var conversation = FirestoreConversationMapper.ToConversation(conversationSnapshot);
        if (conversation == null || !FirestoreConversationSchema.OwnsConversation(conversationSnapshot, user))
        {
            return null;
        }

        var uid = FirestoreConversationSchema.UserDocumentId(user);
        var messagesSnapshot = await _db.Collection(FirestoreConversationSchema.MessagesCollection)
            .WhereEqualTo(FirestoreConversationSchema.Uid, uid)
            .WhereEqualTo(FirestoreConversationSchema.ConversationId, conversationId.ToString("D"))
            .OrderBy(FirestoreConversationSchema.CreatedAt)
            .GetSnapshotAsync();

        conversation.Messages = messagesSnapshot.Documents
            .Select(FirestoreConversationMapper.ToMessage)
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

        await GetConversationDocument(conversation.Id).SetAsync(
            FirestoreConversationMapper.ToDocument(
                conversation,
                FirestoreConversationSchema.UserDocumentId(user)));

        return conversation;
    }

    public async Task<IReadOnlyList<ChatHistoryEntry>> GetHistoryAsync(
        Guid conversationId,
        AuthenticatedUser user,
        int maxMessages)
    {
        var conversationSnapshot = await GetConversationDocument(conversationId).GetSnapshotAsync();
        if (!FirestoreConversationSchema.OwnsConversation(conversationSnapshot, user))
        {
            return [];
        }

        var uid = FirestoreConversationSchema.UserDocumentId(user);
        var take = Math.Max(1, maxMessages);
        var snapshot = await _db.Collection(FirestoreConversationSchema.MessagesCollection)
            .WhereEqualTo(FirestoreConversationSchema.Uid, uid)
            .WhereEqualTo(FirestoreConversationSchema.ConversationId, conversationId.ToString("D"))
            .OrderByDescending(FirestoreConversationSchema.CreatedAt)
            .Limit(take)
            .GetSnapshotAsync();

        return snapshot.Documents
            .Select(FirestoreConversationMapper.ToMessage)
            .Where(message => message != null)
            .Cast<Message>()
            .OrderBy(message => message.CreatedAt)
            .Select(message => new ChatHistoryEntry(
                message.Role,
                message.Content,
                message.ThinkingContent,
                ChatAttachmentSerializer.Deserialize(message.AttachmentsJson)))
            .ToList();
    }

    public async Task<Message?> AddMessageAsync(
        Guid conversationId,
        AuthenticatedUser user,
        string role,
        string content,
        string? thinkingContent = null,
        IReadOnlyCollection<ChatAttachment>? attachments = null)
    {
        var conversationRef = GetConversationDocument(conversationId);
        var conversationSnapshot = await conversationRef.GetSnapshotAsync();
        if (!FirestoreConversationSchema.OwnsConversation(conversationSnapshot, user))
        {
            return null;
        }

        var attachmentsJson = ChatAttachmentSerializer.Serialize(attachments);
        var message = new Message
        {
            Id = Guid.NewGuid(),
            ConversationId = conversationId,
            Role = role,
            Content = content,
            ThinkingContent = thinkingContent,
            AttachmentsJson = attachmentsJson
        };

        await _db.Collection(FirestoreConversationSchema.MessagesCollection)
            .Document(message.Id.ToString("D"))
            .SetAsync(FirestoreConversationMapper.ToDocument(
                message,
                FirestoreConversationSchema.UserDocumentId(user),
                user.LocalUserId));

        var updates = new Dictionary<string, object?>
        {
            [FirestoreConversationSchema.UpdatedAt] = Timestamp.FromDateTime(DateTime.UtcNow)
        };

        if (role == "user"
            && (!conversationSnapshot.TryGetValue<string>(FirestoreConversationSchema.Title, out var title)
                || string.IsNullOrWhiteSpace(title)))
        {
            updates[FirestoreConversationSchema.Title] = CreateTitle(content, attachments?.Count ?? 0);
        }

        await conversationRef.UpdateAsync(updates);
        return message;
    }

    public async Task UpdateTitleAsync(Guid conversationId, AuthenticatedUser user, string title)
    {
        var conversationRef = GetConversationDocument(conversationId);
        var snapshot = await conversationRef.GetSnapshotAsync();
        if (!FirestoreConversationSchema.OwnsConversation(snapshot, user))
        {
            return;
        }

        await conversationRef.UpdateAsync(new Dictionary<string, object>
        {
            [FirestoreConversationSchema.Title] = title,
            [FirestoreConversationSchema.UpdatedAt] = Timestamp.FromDateTime(DateTime.UtcNow)
        });
    }

    public async Task DeleteConversationAsync(Guid conversationId, AuthenticatedUser user)
    {
        var conversationRef = GetConversationDocument(conversationId);
        var snapshot = await conversationRef.GetSnapshotAsync();
        if (!FirestoreConversationSchema.OwnsConversation(snapshot, user))
        {
            return;
        }

        var uid = FirestoreConversationSchema.UserDocumentId(user);
        var messages = await _db.Collection(FirestoreConversationSchema.MessagesCollection)
            .WhereEqualTo(FirestoreConversationSchema.Uid, uid)
            .WhereEqualTo(FirestoreConversationSchema.ConversationId, conversationId.ToString("D"))
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
        if (!FirestoreConversationSchema.OwnsConversation(snapshot, user))
        {
            return;
        }

        await conversationRef.UpdateAsync(new Dictionary<string, object>
        {
            [FirestoreConversationSchema.TokenCount] = tokenCount,
            [FirestoreConversationSchema.UpdatedAt] = Timestamp.FromDateTime(DateTime.UtcNow)
        });
    }

    private DocumentReference GetConversationDocument(Guid conversationId) =>
        _db.Collection(FirestoreConversationSchema.ConversationsCollection).Document(conversationId.ToString("D"));

    private static string CreateTitle(string content, int attachmentCount)
    {
        var fallbackTitle = attachmentCount > 0
            ? $"Attachment request ({attachmentCount})"
            : "Untitled";
        var titleSource = string.IsNullOrWhiteSpace(content) ? fallbackTitle : content;
        return titleSource.Length > 50 ? titleSource[..50] + "..." : titleSource;
    }
}
