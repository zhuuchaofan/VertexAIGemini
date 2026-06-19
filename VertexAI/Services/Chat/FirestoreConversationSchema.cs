using Google.Cloud.Firestore;
using VertexAI.Services.Auth;
using VertexAI.Services.Firestore;

namespace VertexAI.Services.Chat;

internal static class FirestoreConversationSchema
{
    public const string ConversationsCollection = "conversations";
    public const string MessagesCollection = "messages";

    public const string Uid = "uid";
    public const string LocalUserId = "localUserId";
    public const string Title = "title";
    public const string ProviderId = "providerId";
    public const string ModelName = "modelName";
    public const string PresetId = "presetId";
    public const string CustomPrompt = "customPrompt";
    public const string HistorySummary = "historySummary";
    public const string TokenCount = "tokenCount";
    public const string CreatedAt = "createdAt";
    public const string UpdatedAt = "updatedAt";
    public const string ConversationId = "conversationId";
    public const string Role = "role";
    public const string Content = "content";
    public const string ThinkingContent = "thinkingContent";
    public const string AttachmentsJson = "attachmentsJson";

    public static string UserDocumentId(AuthenticatedUser user) =>
        FirestoreDocumentIds.UserDocumentId(user.LocalUserId, user.FirebaseUid);

    public static bool OwnsConversation(DocumentSnapshot snapshot, AuthenticatedUser user) =>
        snapshot.Exists
        && snapshot.TryGetValue<string>(Uid, out var uid)
        && string.Equals(uid, UserDocumentId(user), StringComparison.Ordinal);
}
