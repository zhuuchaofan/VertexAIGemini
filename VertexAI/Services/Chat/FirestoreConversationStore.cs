using Google.Cloud.Firestore;
using VertexAI.Services.Attachments;
using VertexAI.Services.Auth;

namespace VertexAI.Services.Chat;

public sealed class FirestoreConversationStore : IConversationStore
{
    private readonly FirestoreDb _db;
    private readonly IChatAttachmentStore _attachments;

    public FirestoreConversationStore(FirestoreDb db, IChatAttachmentStore attachments)
    {
        _db = db;
        _attachments = attachments;
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

    public async Task<List<Conversation>> GetUserConversationsPageAsync(
        AuthenticatedUser user,
        string? cursor,
        int limit)
    {
        var uid = FirestoreConversationSchema.UserDocumentId(user);
        var query = _db.Collection(FirestoreConversationSchema.ConversationsCollection)
            .WhereEqualTo(FirestoreConversationSchema.Uid, uid)
            .OrderByDescending(FirestoreConversationSchema.UpdatedAt);

        if (TryParseCursor(cursor, out var cursorUpdatedAt))
        {
            query = query.StartAfter(Timestamp.FromDateTime(cursorUpdatedAt));
        }

        var snapshot = await query
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

        var messages = messagesSnapshot.Documents
            .Select(FirestoreConversationMapper.ToMessage)
            .Where(message => message != null)
            .Cast<Message>()
            .ToList();

        foreach (var message in messages)
        {
            message.AttachmentsJson = await LoadAttachmentsJsonAsync(message.AttachmentsJson);
        }

        conversation.Messages = messages;
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

        var messages = snapshot.Documents
            .Select(FirestoreConversationMapper.ToMessage)
            .Where(message => message != null)
            .Cast<Message>()
            .OrderBy(message => message.CreatedAt)
            .ToList();

        foreach (var message in messages)
        {
            message.AttachmentsJson = await LoadAttachmentsJsonAsync(message.AttachmentsJson);
        }

        return messages
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

        var message = new Message
        {
            Id = Guid.NewGuid(),
            ConversationId = conversationId,
            Role = role,
            Content = content,
            ThinkingContent = thinkingContent
        };
        var storedAttachments = await SaveAttachmentsAsync(
            attachments,
            conversationId,
            message.Id);
        var inlineError = _attachments.IsEnabled
            ? null
            : ChatAttachmentValidator.ValidateInlineFirestorePayload(storedAttachments);
        if (inlineError != null)
        {
            throw new InvalidOperationException(inlineError);
        }

        message.AttachmentsJson = ChatAttachmentSerializer.Serialize(storedAttachments);

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
            if (message.TryGetValue<string>(FirestoreConversationSchema.AttachmentsJson, out var attachmentsJson))
            {
                foreach (var attachment in ChatAttachmentSerializer.Deserialize(attachmentsJson))
                {
                    await _attachments.DeleteAsync(attachment);
                }
            }

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

    private static bool TryParseCursor(string? cursor, out DateTime updatedAt)
    {
        updatedAt = default;
        return !string.IsNullOrWhiteSpace(cursor)
            && DateTime.TryParse(
                cursor,
                System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.AdjustToUniversal | System.Globalization.DateTimeStyles.AssumeUniversal,
                out updatedAt);
    }

    private async Task<IReadOnlyList<ChatAttachment>> SaveAttachmentsAsync(
        IReadOnlyCollection<ChatAttachment>? attachments,
        Guid conversationId,
        Guid messageId)
    {
        if (attachments is not { Count: > 0 })
        {
            return [];
        }

        var stored = new List<ChatAttachment>(attachments.Count);
        var index = 0;
        foreach (var attachment in attachments)
        {
            stored.Add(await _attachments.SaveAsync(attachment, conversationId, messageId, index));
            index++;
        }

        return stored;
    }

    private async Task<string?> LoadAttachmentsJsonAsync(string? attachmentsJson)
    {
        var attachments = ChatAttachmentSerializer.Deserialize(attachmentsJson);
        if (attachments.Count == 0)
        {
            return attachmentsJson;
        }

        var loaded = new List<ChatAttachment>(attachments.Count);
        foreach (var attachment in attachments)
        {
            loaded.Add(await _attachments.LoadAsync(attachment));
        }

        return ChatAttachmentSerializer.Serialize(loaded);
    }

    private static string CreateTitle(string content, int attachmentCount)
    {
        var fallbackTitle = attachmentCount > 0
            ? $"Attachment request ({attachmentCount})"
            : "Untitled";
        var titleSource = string.IsNullOrWhiteSpace(content) ? fallbackTitle : content;
        return titleSource.Length > 50 ? titleSource[..50] + "..." : titleSource;
    }
}
