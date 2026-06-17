using Google.Cloud.Firestore;
using VertexAI.Services.Auth;
using VertexAI.Services.Firestore;

namespace VertexAI.Services.UserSettings;

public sealed class FirestoreUserSettingsStore : IUserSettingsStore
{
    private readonly FirestoreDb _db;

    public FirestoreUserSettingsStore(FirestoreDb db)
    {
        _db = db;
    }

    public async Task<string?> GetDefaultAssistantPromptAsync(AuthenticatedUser user)
    {
        var snapshot = await GetUserDocument(user).GetSnapshotAsync();
        if (!snapshot.Exists)
        {
            return null;
        }

        return snapshot.TryGetValue<string>("defaultAssistantPrompt", out var prompt)
            ? prompt
            : null;
    }

    public async Task<string?> UpdateDefaultAssistantPromptAsync(
        AuthenticatedUser user,
        string? defaultAssistantPrompt)
    {
        var prompt = string.IsNullOrWhiteSpace(defaultAssistantPrompt)
            ? null
            : defaultAssistantPrompt;

        var updates = new Dictionary<string, object?>
        {
            ["defaultAssistantPrompt"] = prompt,
            ["email"] = user.Email,
            ["updatedAt"] = Timestamp.FromDateTime(DateTime.UtcNow)
        };

        await GetUserDocument(user).SetAsync(updates, SetOptions.MergeAll);
        return prompt;
    }

    private DocumentReference GetUserDocument(AuthenticatedUser user)
    {
        var userId = FirestoreDocumentIds.UserDocumentId(user.LocalUserId, user.FirebaseUid);
        return _db.Collection("users").Document(userId);
    }
}
