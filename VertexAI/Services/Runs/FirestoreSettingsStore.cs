using Google.Cloud.Firestore;
using VertexAI.Services.Auth;
using VertexAI.Services.Chat;

namespace VertexAI.Services.Runs;

/// <summary>
/// Layer one: <c>userSettings/{uid}.chatDefaults</c>. Layer two: <c>conversations/{id}.settings</c>. Both are optional
/// fields; documents written before they existed simply have none.
/// </summary>
public sealed class FirestoreSettingsStore(FirestoreDb db) : ISettingsStore
{
    public const string UserSettingsCollection = "userSettings";
    private const string DefaultsField = "chatDefaults";

    private static string Uid(AuthenticatedUser user) => FirestoreConversationSchema.UserDocumentId(user);

    public async Task<ChatSettingsDocument?> GetDefaultsAsync(AuthenticatedUser user, CancellationToken cancellationToken)
    {
        var snapshot = await db.Collection(UserSettingsCollection).Document(Uid(user)).GetSnapshotAsync(cancellationToken);
        return snapshot.Exists && snapshot.TryGetValue<Dictionary<string, object>>(DefaultsField, out var map)
            ? SettingsMapper.FromMap(map)
            : null;
    }

    public async Task SetDefaultsAsync(AuthenticatedUser user, ChatSettingsDocument settings, CancellationToken cancellationToken)
    {
        // Defaults initialize new conversations; the prompt text belongs to a conversation, never to the defaults.
        var map = SettingsMapper.ToMap(settings with { CustomPrompt = null }, DateTime.UtcNow);
        await db.Collection(UserSettingsCollection).Document(Uid(user)).SetAsync(
            new Dictionary<string, object>
            {
                ["schemaVersion"] = 1,
                [DefaultsField] = map,
                [FirestoreConversationSchema.UpdatedAt] = FieldValue.ServerTimestamp
            },
            SetOptions.MergeAll,
            cancellationToken);
    }

    public async Task<bool> SetConversationSettingsAsync(
        AuthenticatedUser user, Guid conversationId, ChatSettingsDocument settings, CancellationToken cancellationToken)
    {
        var reference = db.Collection(FirestoreConversationSchema.ConversationsCollection).Document(conversationId.ToString("D"));
        return await db.RunTransactionAsync(async transaction =>
        {
            var snapshot = await transaction.GetSnapshotAsync(reference, cancellationToken);
            if (!FirestoreConversationSchema.OwnsConversation(snapshot, user)) return false;

            // The conversation's own columns follow its settings so older readers keep seeing the current model.
            transaction.Update(reference, new Dictionary<string, object>
            {
                [FirestoreConversationSchema.SettingsMap] = SettingsMapper.ToMap(settings, DateTime.UtcNow),
                [FirestoreConversationSchema.ProviderId] = settings.ProviderId,
                [FirestoreConversationSchema.ModelName] = settings.ModelName,
                [FirestoreConversationSchema.PresetId] = settings.PresetId,
                [FirestoreConversationSchema.CustomPrompt] = settings.CustomPrompt ?? (object)FieldValue.Delete
            });
            return true;
        }, cancellationToken: cancellationToken);
    }
}

internal static class SettingsMapper
{
    public static Dictionary<string, object> ToMap(ChatSettingsDocument settings, DateTime now)
    {
        var map = new Dictionary<string, object>
        {
            ["version"] = 1,
            ["providerId"] = settings.ProviderId,
            ["modelName"] = settings.ModelName,
            ["presetId"] = settings.PresetId,
            ["searchMode"] = settings.SearchMode,
            ["updatedAt"] = Timestamp.FromDateTime(now.ToUniversalTime())
        };
        if (!string.IsNullOrEmpty(settings.CustomPrompt)) map["customPrompt"] = settings.CustomPrompt;
        if (settings.Thinking is { } thinking)
        {
            var thinkingMap = new Dictionary<string, object>();
            if (thinking.Level != null) thinkingMap["level"] = thinking.Level;
            if (thinking.Enabled is { } enabled) thinkingMap["enabled"] = enabled;
            if (thinking.Budget is { } budget) thinkingMap["budget"] = budget;
            if (thinkingMap.Count > 0) map["thinking"] = thinkingMap;
        }

        if (settings.Advanced is { } advanced)
        {
            var advancedMap = new Dictionary<string, object>();
            if (advanced.Temperature is { } temperature) advancedMap["temperature"] = temperature;
            if (advanced.TopP is { } topP) advancedMap["topP"] = topP;
            if (advanced.MaxOutputTokens is { } maxOutput) advancedMap["maxOutputTokens"] = maxOutput;
            if (!string.IsNullOrWhiteSpace(advanced.SafetyThreshold)) advancedMap["safetyThreshold"] = advanced.SafetyThreshold;
            if (advancedMap.Count > 0) map["advanced"] = advancedMap;
        }

        return map;
    }

    private static double? Num(Dictionary<string, object> map, string key) =>
        map.TryGetValue(key, out var value) && value is not null ? Convert.ToDouble(value) : null;

    private static int? Int(Dictionary<string, object> map, string key) =>
        map.TryGetValue(key, out var value) && value is not null ? Convert.ToInt32(value) : null;

    public static ChatSettingsDocument FromMap(Dictionary<string, object> map)
    {
        ThinkingChoice? thinking = null;
        if (map.GetValueOrDefault("thinking") is Dictionary<string, object> t)
            thinking = new ThinkingChoice(t.GetValueOrDefault("level") as string, t.GetValueOrDefault("enabled") as bool?, Int(t, "budget"));
        ChatAdvancedOptions? advanced = null;
        if (map.GetValueOrDefault("advanced") is Dictionary<string, object> a)
            advanced = new ChatAdvancedOptions(a.GetValueOrDefault("safetyThreshold") as string, Num(a, "temperature"), Num(a, "topP"), Int(a, "maxOutputTokens"));
        DateTime? updatedAt = map.GetValueOrDefault("updatedAt") is Timestamp ts ? ts.ToDateTime() : null;
        return new ChatSettingsDocument(
            map.GetValueOrDefault("providerId") as string ?? "",
            map.GetValueOrDefault("modelName") as string ?? "",
            map.GetValueOrDefault("presetId") as string ?? "default",
            map.GetValueOrDefault("customPrompt") as string,
            map.GetValueOrDefault("searchMode") as string ?? SearchModes.Auto,
            thinking, advanced, updatedAt);
    }
}
