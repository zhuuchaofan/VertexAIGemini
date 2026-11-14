using System.Text;
using Google.Cloud.Firestore;
using VertexAI.Services.Auth;
using VertexAI.Services.Chat;
using VertexAI.Services.Runs;

namespace VertexAI.Services.Memory;

/// <summary>One thing the person asked the assistant to remember.</summary>
public sealed record MemoryItem(string Id, string Text, DateTime CreatedAt);

/// <summary>
/// What the assistant remembers about one person, and whether it uses it. Only the person adds to it: the model never
/// writes here.
/// </summary>
public sealed record UserMemory(bool Enabled, IReadOnlyList<MemoryItem> Items)
{
    public static readonly UserMemory Empty = new(true, []);
}

public interface IUserMemoryStore
{
    Task<UserMemory> GetAsync(AuthenticatedUser user, CancellationToken cancellationToken);

    /// <summary>Null when the person already keeps the maximum number of items.</summary>
    Task<MemoryItem?> AddAsync(AuthenticatedUser user, string text, CancellationToken cancellationToken);

    Task<bool> RemoveAsync(AuthenticatedUser user, string id, CancellationToken cancellationToken);

    Task SetEnabledAsync(AuthenticatedUser user, bool enabled, CancellationToken cancellationToken);
}

public static class MemoryRules
{
    public const int MaxItems = 50;
    public const int MaxChars = 300;

    /// <summary>Item ids are minted by the server as 32 hex characters.</summary>
    public static bool IsItemId(string? id) => id is { Length: 32 } && id.All(Uri.IsHexDigit);

    public static (string Text, string? Error) Validate(string? text)
    {
        var trimmed = (text ?? "").Trim();
        if (trimmed.Length == 0) return ("", "要记住的内容不能为空。");
        if (trimmed.Length > MaxChars) return ("", $"每条记忆最多 {MaxChars} 个字。");
        return (trimmed, null);
    }

    /// <summary>
    /// The text added to the system prompt, or null when there is nothing to add (memory off, or empty). The items are
    /// the person's own words; the model is asked to use them where they help and not to recite them.
    /// </summary>
    public static string? ToSystemAddendum(UserMemory memory)
    {
        if (!memory.Enabled || memory.Items.Count == 0) return null;

        var builder = new StringBuilder();
        builder.AppendLine("[关于用户：以下是用户自己保存、希望你长期记住的信息。在相关时自然地参考；不要逐条复述，除非用户问起，也不要提到“记忆”这个功能。]");
        foreach (var item in memory.Items.OrderBy(item => item.CreatedAt))
        {
            builder.Append("- ").AppendLine(item.Text.ReplaceLineEndings(" "));
        }
        return builder.ToString().TrimEnd();
    }

    /// <summary>A system prompt with the memory text after it.</summary>
    public static string Combine(string? systemPrompt, string? addendum) =>
        string.IsNullOrWhiteSpace(addendum)
            ? systemPrompt ?? ""
            : string.IsNullOrWhiteSpace(systemPrompt) ? addendum : $"{systemPrompt}\n\n{addendum}";
}

/// <summary>Kept as one document, <c>userSettings/{uid}/memory/profile</c>, so a run reads it with a single read.</summary>
public sealed class FirestoreUserMemoryStore(FirestoreDb db) : IUserMemoryStore
{
    private DocumentReference Document(AuthenticatedUser user) =>
        db.Collection(FirestoreSettingsStore.UserSettingsCollection)
            .Document(FirestoreConversationSchema.UserDocumentId(user))
            .Collection("memory")
            .Document("profile");

    private static UserMemory Read(DocumentSnapshot snapshot)
    {
        if (!snapshot.Exists) return UserMemory.Empty;
        var enabled = !snapshot.TryGetValue<bool>("enabled", out var flag) || flag;
        var items = new List<MemoryItem>();
        if (snapshot.TryGetValue<List<Dictionary<string, object>>>("items", out var stored))
        {
            foreach (var entry in stored)
            {
                if (entry.TryGetValue("id", out var id) && id is string idText
                    && entry.TryGetValue("text", out var text) && text is string body)
                {
                    var created = entry.TryGetValue("createdAt", out var at) && at is Timestamp stamp ? stamp.ToDateTime() : DateTime.MinValue;
                    items.Add(new MemoryItem(idText, body, created));
                }
            }
        }
        return new UserMemory(enabled, items);
    }

    private static Dictionary<string, object> Write(MemoryItem item) => new()
    {
        ["id"] = item.Id,
        ["text"] = item.Text,
        ["createdAt"] = Timestamp.FromDateTime(DateTime.SpecifyKind(item.CreatedAt, DateTimeKind.Utc))
    };

    public async Task<UserMemory> GetAsync(AuthenticatedUser user, CancellationToken cancellationToken) =>
        Read(await Document(user).GetSnapshotAsync(cancellationToken));

    public async Task<MemoryItem?> AddAsync(AuthenticatedUser user, string text, CancellationToken cancellationToken)
    {
        var reference = Document(user);
        var item = new MemoryItem(Guid.NewGuid().ToString("N"), text, DateTime.UtcNow);
        var added = await db.RunTransactionAsync(async transaction =>
        {
            var current = Read(await transaction.GetSnapshotAsync(reference, cancellationToken));
            if (current.Items.Count >= MemoryRules.MaxItems) return false;
            transaction.Set(reference, new Dictionary<string, object>
            {
                ["items"] = current.Items.Append(item).Select(Write).ToList(),
                ["updatedAt"] = Timestamp.FromDateTime(DateTime.UtcNow)
            }, SetOptions.MergeAll);
            return true;
        }, cancellationToken: cancellationToken);
        return added ? item : null;
    }

    public async Task<bool> RemoveAsync(AuthenticatedUser user, string id, CancellationToken cancellationToken)
    {
        if (!MemoryRules.IsItemId(id)) return false;
        var reference = Document(user);
        return await db.RunTransactionAsync(async transaction =>
        {
            var current = Read(await transaction.GetSnapshotAsync(reference, cancellationToken));
            if (current.Items.All(item => item.Id != id)) return false;
            transaction.Set(reference, new Dictionary<string, object>
            {
                ["items"] = current.Items.Where(item => item.Id != id).Select(Write).ToList(),
                ["updatedAt"] = Timestamp.FromDateTime(DateTime.UtcNow)
            }, SetOptions.MergeAll);
            return true;
        }, cancellationToken: cancellationToken);
    }

    public Task SetEnabledAsync(AuthenticatedUser user, bool enabled, CancellationToken cancellationToken) =>
        Document(user).SetAsync(new Dictionary<string, object>
        {
            ["enabled"] = enabled,
            ["updatedAt"] = Timestamp.FromDateTime(DateTime.UtcNow)
        }, SetOptions.MergeAll, cancellationToken);
}
